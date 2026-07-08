using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YamlDotNet.Serialization.NamingConventions;

namespace Allegiance.Factions.Schema;

/// <summary>
/// Generates a JSON Schema (draft 2020-12) describing the YAML shape produced by
/// <c>CoreSerializer</c>, using <see cref="JsonSchemaExporter"/> /
/// <c>JsonSerializerOptions.GetJsonSchemaAsNode</c>. Property names, dictionary keys, and enum values
/// use the same kebab-case convention as the serializer — the schema keys are byte-identical to the
/// authored YAML — so the schema can validate the content files (e.g. via the VS Code YAML
/// extension). Any POCO root that <c>CoreSerializer.Deserialize&lt;T&gt;</c> reads (including the
/// server's <c>WorldDef</c>/<c>MapDef</c>) can be schematized here since they share this convention.
///
/// XML <c>&lt;summary&gt;</c> doc comments on the model types/properties are surfaced as schema
/// <c>description</c> fields (so they show as hover text in the YAML editor). This requires the model
/// assemblies to be built with <c>&lt;GenerateDocumentationFile&gt;true&lt;/GenerateDocumentationFile&gt;</c>
/// so the <c>.xml</c> doc file sits next to the <c>.dll</c>.
/// </summary>
public static class YamlJsonSchema
{
    // Delegates to YamlDotNet's own HyphenatedNamingConvention so STJ emits exactly the kebab-case
    // names CoreSerializer writes (avoids KebabCaseLower-vs-Hyphenated divergence on edge cases).
    private sealed class HyphenatedPolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) =>
            HyphenatedNamingConvention.Instance.Apply(name);
    }

    // Mirrors CoreSerializer's config: kebab-case properties + dictionary keys, and enums serialized
    // as kebab-case strings. A TypeInfoResolver is required by JsonSchemaExporter.
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var naming = new HyphenatedPolicy();
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = naming,
            DictionaryKeyPolicy = naming,
            Converters = { new JsonStringEnumConverter(naming) },
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
    }

    /// <summary>Builds the schema for <paramref name="rootType"/> as a <see cref="JsonNode"/>.</summary>
    public static JsonNode GenerateNode(Type rootType)
    {
        // Summaries can come from the factions assembly (Core/Faction/…) or the root type's own
        // assembly (e.g. the server's WorldDef/MapDef). Merge both doc sets.
        var docs = XmlDocs.For(typeof(YamlJsonSchema).Assembly, rootType.Assembly);

        var exporterOptions = new JsonSchemaExporterOptions
        {
            // Nullable-oblivious members (nullable ref types) stay nullable — matches YamlDotNet,
            // which treats every field as optional. No `required` keywords are emitted either way.
            TreatNullObliviousAsNonNullable = false,
            TransformSchemaNode = (context, node) =>
            {
                if (node is not JsonObject obj)
                    return node;

                // Surface the XML <summary> as `description`: property doc for a member node, else
                // the type doc for an object node.
                if (!obj.ContainsKey("description"))
                {
                    string? desc = context.PropertyInfo?.AttributeProvider is MemberInfo member
                        ? docs.ForMember(member)
                        : context.PropertyInfo is null
                            ? docs.ForType(context.TypeInfo.Type)
                            : null;
                    if (!string.IsNullOrEmpty(desc))
                        obj["description"] = desc;
                }

                // Reject unknown keys on POCO objects (mirrors the old generator's strictness), but
                // leave dictionaries alone — their `additionalProperties` is a value schema, not a bool.
                if (context.TypeInfo.Kind == JsonTypeInfoKind.Object
                    && obj.ContainsKey("properties")
                    && !obj.ContainsKey("additionalProperties"))
                {
                    obj["additionalProperties"] = false;
                }
                return node;
            },
        };

        var schema = Options.GetJsonSchemaAsNode(rootType, exporterOptions);
        // Stamp the dialect on the root so validators pick draft 2020-12 explicitly.
        if (schema is JsonObject root && !root.ContainsKey("$schema"))
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return schema;
    }

    /// <summary>Builds the pretty-printed schema JSON for <paramref name="rootType"/>.</summary>
    public static string Generate(Type rootType) =>
        GenerateNode(rootType).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Reads compiled XML documentation files (the <c>.xml</c> emitted next to an assembly when
    /// <c>GenerateDocumentationFile</c> is on) and resolves the <c>&lt;summary&gt;</c> text for a type
    /// or member. Assembly doc sets are cached; a missing <c>.xml</c> just yields no descriptions.
    /// </summary>
    private sealed class XmlDocs
    {
        private static readonly ConcurrentDictionary<Assembly, XmlDocs> Cache = new();
        private readonly Dictionary<string, string> _summaries;

        private XmlDocs(Dictionary<string, string> summaries) => _summaries = summaries;

        public static XmlDocs For(params Assembly[] assemblies)
        {
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asm in assemblies)
            {
                var docs = Cache.GetOrAdd(asm, Load);
                foreach (var (key, value) in docs._summaries)
                    merged[key] = value;
            }
            return new XmlDocs(merged);
        }

        private static XmlDocs Load(Assembly asm)
        {
            var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
            var location = asm.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var xmlPath = Path.ChangeExtension(location, ".xml");
                if (File.Exists(xmlPath))
                {
                    foreach (var member in XDocument.Load(xmlPath).Descendants("member"))
                    {
                        var name = member.Attribute("name")?.Value;
                        var summary = member.Element("summary");
                        if (name is not null && summary is not null)
                            summaries[name] = Collapse(Render(summary));
                    }
                }
            }
            return new XmlDocs(summaries);
        }

        public string? ForType(Type type) =>
            type.FullName is { } name && _summaries.TryGetValue("T:" + name, out var s) ? s : null;

        public string? ForMember(MemberInfo member) =>
            member.DeclaringType?.FullName is { } declaring
            && _summaries.TryGetValue("P:" + declaring + "." + member.Name, out var s)
                ? s : null;

        // Render a <summary> to plain text: keep text/<c>/<para> inner text, and turn inline
        // references (<see cref="P:...Foo"/>, <paramref name="x"/>) into their short identifier so
        // self-closed elements don't leave gaps in the sentence.
        private static string Render(XElement element)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        sb.Append(text.Value);
                        break;
                    case XElement child when child.Name.LocalName is "see" or "seealso":
                        var cref = child.Attribute("cref")?.Value ?? child.Attribute("langword")?.Value ?? "";
                        var text2 = child.Value; // <see cref="..">explicit text</see>
                        sb.Append(!string.IsNullOrEmpty(text2) ? text2 : ShortName(cref));
                        break;
                    case XElement child when child.Name.LocalName == "paramref" || child.Name.LocalName == "typeparamref":
                        sb.Append(child.Attribute("name")?.Value ?? "");
                        break;
                    case XElement child:
                        sb.Append(Render(child)); // <c>, <para>, <b>, … keep inner content
                        break;
                }
            }
            return sb.ToString();
        }

        // "P:Allegiance.Factions.Model.Faction.Id" -> "Id"; "T:...Hull" -> "Hull".
        private static string ShortName(string cref)
        {
            var s = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
            var dot = s.LastIndexOf('.');
            return dot >= 0 ? s[(dot + 1)..] : s;
        }

        // XML doc summaries carry the source's newlines/indentation — collapse to a single line.
        private static string Collapse(string text) =>
            Regex.Replace(text, @"\s+", " ").Trim();
    }
}

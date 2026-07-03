using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Allegiance.Factions.Cli;

/// <summary>
/// Generates a JSON Schema (draft 2020-12) describing the YAML shape produced by
/// <c>CoreSerializer</c>. Property names and enum values use the same kebab-case convention as the
/// serializer, so the schema can validate the authored YAML files (e.g. via the VS Code YAML
/// extension). Output is deterministic — definitions and properties are sorted — so a regenerated
/// schema only diffs when the model actually changes.
/// </summary>
internal static class JsonSchemaGenerator
{
    private static readonly INamingConvention Naming = HyphenatedNamingConvention.Instance;

    /// <summary>
    /// Builds a schema document whose root references the first type, with every reachable complex
    /// type and enum emitted under <c>$defs</c>. Extra root types (e.g. <c>Faction</c>, <c>Manifest</c>)
    /// are included so per-file schemas can reference them via <c>#/$defs/&lt;Type&gt;</c>.
    /// </summary>
    public static string Generate(params Type[] rootTypes)
    {
        if (rootTypes.Length == 0)
            throw new ArgumentException("At least one root type is required.", nameof(rootTypes));

        var defs = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        var queue = new Queue<Type>();
        var seen = new HashSet<Type>();

        foreach (var type in rootTypes)
            Enqueue(type, queue, seen);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            defs[DefName(type)] = BuildDefinition(type, queue, seen);
        }

        var defsObject = new JsonObject();
        foreach (var (name, schema) in defs)
            defsObject[name] = schema;

        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Allegiance faction core",
            ["$ref"] = $"#/$defs/{DefName(rootTypes[0])}",
            ["$defs"] = defsObject,
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        });
    }

    private static JsonObject BuildDefinition(Type type, Queue<Type> queue, HashSet<Type> seen)
    {
        if (type.IsEnum)
        {
            var values = new JsonArray();
            foreach (var name in Enum.GetNames(type))
                values.Add(Naming.Apply(name));
            return new JsonObject { ["type"] = "string", ["enum"] = values };
        }

        var properties = new JsonObject();
        foreach (var property in OrderedProperties(type))
            properties[Naming.Apply(property.Name)] = SchemaFor(property.PropertyType, queue, seen);

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
        };
    }

    private static IEnumerable<PropertyInfo> OrderedProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.GetMethod is { IsPublic: true })
            .Where(p => p.Name != "EqualityContract") // compiler-generated on records
            .OrderBy(p => Naming.Apply(p.Name), StringComparer.Ordinal);

    private static JsonObject SchemaFor(Type type, Queue<Type> queue, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (IsInteger(type)) return new JsonObject { ["type"] = "integer" };
        if (IsNumber(type)) return new JsonObject { ["type"] = "number" };

        if (type.IsEnum)
        {
            Enqueue(type, queue, seen);
            return Ref(type);
        }

        if (TryGetDictionary(type, out var keyType, out var valueType))
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = SchemaFor(valueType, queue, seen),
            };
            if (keyType.IsEnum)
            {
                Enqueue(keyType, queue, seen);
                schema["propertyNames"] = Ref(keyType);
            }
            return schema;
        }

        if (TryGetEnumerable(type, out var elementType))
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = SchemaFor(elementType, queue, seen),
            };
        }

        Enqueue(type, queue, seen);
        return Ref(type);
    }

    private static void Enqueue(Type type, Queue<Type> queue, HashSet<Type> seen)
    {
        if (seen.Add(type))
            queue.Enqueue(type);
    }

    private static JsonObject Ref(Type type) => new() { ["$ref"] = $"#/$defs/{DefName(type)}" };

    private static string DefName(Type type) => type.Name;

    private static bool IsInteger(Type type) =>
        type == typeof(sbyte) || type == typeof(byte)
        || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong);

    private static bool IsNumber(Type type) =>
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    private static bool TryGetDictionary(Type type, out Type keyType, out Type valueType)
    {
        var dictionary = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionary is not null)
        {
            var args = dictionary.GetGenericArguments();
            keyType = args[0];
            valueType = args[1];
            return true;
        }

        keyType = valueType = typeof(object);
        return false;
    }

    private static bool TryGetEnumerable(Type type, out Type elementType)
    {
        var enumerable = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null && typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(object);
        return false;
    }
}

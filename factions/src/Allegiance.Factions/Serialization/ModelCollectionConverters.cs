using Allegiance.Factions.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Allegiance.Factions.Serialization;

/// <summary>
/// READ-side YAML converters for the model's three custom collections. The static generator behind
/// <see cref="FactionsYamlContext"/> recognises only the BCL <c>List</c>/<c>Dictionary</c> types (by
/// name), so a <see cref="TechSet"/> (<c>HashSet&lt;string&gt;</c>), a <see cref="CapabilitySet"/>
/// (<c>HashSet&lt;Capability&gt;</c>) or an <see cref="AttributeModifiers"/>
/// (<c>Dictionary&lt;GameAttribute, double&gt;</c>) would otherwise be treated as a plain object.
/// </summary>
/// <remarks>
/// Every element goes back through YamlDotNet's own deserializer (<c>rootDeserializer</c>), so scalar
/// and enum parsing - kebab-case enum names included - is the library's, not a second copy. The
/// semantics match what the reflection deserializer did for these types: a null node yields null, a
/// repeated set entry is merged, a repeated map key keeps the last value. Registered on the
/// DESERIALIZER only: the (reflection, tooling-only) serializer still writes them as the plain
/// sequence / mapping they are.
/// </remarks>
internal static class ModelCollectionConverters
{
    public static readonly IYamlTypeConverter[] All =
    [
        new SetConverter<TechSet, string>(),
        new SetConverter<CapabilitySet, Capability>(),
        new AttributeModifiersConverter(),
    ];

    // Same test as YamlDotNet's NullNodeDeserializer, which a type converter runs AHEAD of.
    private static bool TryConsumeNull(IParser parser)
    {
        if (!parser.Accept<NodeEvent>(out var node))
            return false;
        bool isNull =
            node.Tag == "tag:yaml.org,2002:null"
            || node is Scalar { Style: ScalarStyle.Plain, IsKey: false, Value: "" or "~" or "null" or "Null" or "NULL" };
        if (isNull)
            parser.SkipThisAndNestedEvents();
        return isNull;
    }

    private sealed class SetConverter<TSet, TItem> : IYamlTypeConverter
        where TSet : HashSet<TItem>, new()
    {
        public bool Accepts(Type type) => type == typeof(TSet);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (TryConsumeNull(parser))
                return null;
            var set = new TSet();
            parser.Consume<SequenceStart>();
            while (!parser.TryConsume<SequenceEnd>(out _))
                set.Add((TItem)rootDeserializer(typeof(TItem))!);
            return set;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException($"{typeof(TSet).Name} is written by the default serializer.");
    }

    private sealed class AttributeModifiersConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(AttributeModifiers);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (TryConsumeNull(parser))
                return null;
            var map = new AttributeModifiers();
            parser.Consume<MappingStart>();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var attribute = (GameAttribute)rootDeserializer(typeof(GameAttribute))!;
                map[attribute] = (double)rootDeserializer(typeof(double))!;
            }
            return map;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException("AttributeModifiers is written by the default serializer.");
    }
}

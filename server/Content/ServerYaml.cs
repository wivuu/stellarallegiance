using Allegiance.Factions.Serialization;
using YamlDotNet.Serialization;

namespace SimServer.Content;

// The YamlDotNet STATIC context for the server's own YAML roots - world.yaml (WorldDef) and the map
// files (MapDef). The server publishes as NativeAOT, where YamlDotNet's reflection deserializer
// throws; the source generator (Vecc.YamlDotNet.Analyzers.StaticGenerator) emits the type inspector
// and object factory for the types listed here instead. The faction/tech-tree bundle has its own
// context in the factions library (FactionsYamlContext) - same generator, same conventions.
//
// A new `*Def` block class = one [YamlSerializable] line here. Forgetting it is a BUILD error
// (YDNG001, see SimServer.csproj), not a boot failure on the first world.yaml that authors the block.
[YamlStaticContext]
// ---- world.yaml ----
[YamlSerializable(typeof(WorldDef))]
[YamlSerializable(typeof(WorldTurretDef))]
[YamlSerializable(typeof(WorldAiDef))]
[YamlSerializable(typeof(WorldCombatDef))]
[YamlSerializable(typeof(WorldMechanicsDef))]
[YamlSerializable(typeof(WorldSalvageDef))]
[YamlSerializable(typeof(WorldSeedingDef))]
[YamlSerializable(typeof(SpecialWeightsDef))]
[YamlSerializable(typeof(WorldMiningDef))]
[YamlSerializable(typeof(WorldConstructorDef))]
[YamlSerializable(typeof(WorldBuildDef))]
[YamlSerializable(typeof(WorldScoringDef))]
// ---- maps/*.yaml ----
[YamlSerializable(typeof(MapDef))]
[YamlSerializable(typeof(MapSectorDef))]
[YamlSerializable(typeof(GarrisonDef))]
[YamlSerializable(typeof(SectorEnvDef))]
[YamlSerializable(typeof(SunDef))]
[YamlSerializable(typeof(NebulaDef))]
[YamlSerializable(typeof(DustDef))]
public partial class ServerYamlContext : StaticContext;

// Reads the server's YAML roots with THE content conventions (kebab-case keys and enum values,
// unmatched keys ignored): the deserializer is built by the factions library, so world/map files
// follow exactly the rules the faction bundle does.
public static class ServerYaml
{
    private static readonly IDeserializer Deserializer = CoreSerializer.BuildDeserializer(new ServerYamlContext());

    // An empty document yields a default instance, as CoreSerializer.Deserialize<T> does.
    public static T Deserialize<T>(string yaml)
        where T : new() => Deserializer.Deserialize<T>(yaml) ?? new T();
}

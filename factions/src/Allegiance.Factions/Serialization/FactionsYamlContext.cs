using Allegiance.Factions.Model;
using Allegiance.Factions.Resolution;
using YamlDotNet.Serialization;

namespace Allegiance.Factions.Serialization;

/// <summary>
/// The YamlDotNet STATIC context for the content model: the source generator
/// (<c>Vecc.YamlDotNet.Analyzers.StaticGenerator</c>) emits a reflection-free type inspector and
/// object factory for every type registered below, which is what lets the sim server load content
/// as a NativeAOT binary (reflection-based YamlDotNet throws there).
/// </summary>
/// <remarks>
/// <para><b>Adding a model type = one line here.</b> Every CONCRETE class that appears in YAML must be
/// registered (abstract bases such as <see cref="Buildable"/> / <see cref="Part"/> /
/// <see cref="Expendable"/> must not be: the generator walks base types itself, and would emit
/// <c>new Part()</c>). <c>List&lt;T&gt;</c>, arrays and <c>Dictionary&lt;,&gt;</c> of registered or
/// primitive types are discovered from the property types. An unregistered type fails loudly at
/// load ("not registered in the YamlDotNet static context"), which every content test would catch.</para>
/// <para>The generator only understands the BCL list/dictionary types by name, so the three custom
/// collections (<see cref="TechSet"/>, <see cref="CapabilitySet"/>, <see cref="AttributeModifiers"/>)
/// are read by the converters in <see cref="ModelCollectionConverters"/> instead.</para>
/// </remarks>
[YamlStaticContext]
// ---- bundle roots ----
[YamlSerializable(typeof(Manifest))]
[YamlSerializable(typeof(Core))]
[YamlSerializable(typeof(Faction))]
// ---- catalog ----
[YamlSerializable(typeof(Tech))]
[YamlSerializable(typeof(Hull))]
[YamlSerializable(typeof(CargoLoad))]
[YamlSerializable(typeof(Hardpoint))]
[YamlSerializable(typeof(TurnRates))]
[YamlSerializable(typeof(Weapon))]
[YamlSerializable(typeof(Shield))]
[YamlSerializable(typeof(Cloak))]
[YamlSerializable(typeof(Afterburner))]
[YamlSerializable(typeof(AmmoPack))]
[YamlSerializable(typeof(Launcher))]
[YamlSerializable(typeof(Station))]
[YamlSerializable(typeof(Development))]
[YamlSerializable(typeof(Drone))]
[YamlSerializable(typeof(Missile))]
[YamlSerializable(typeof(Mine))]
[YamlSerializable(typeof(Chaff))]
[YamlSerializable(typeof(Probe))]
[YamlSerializable(typeof(FuelPod))]
[YamlSerializable(typeof(Projectile))]
// ---- tech-tree report (the CLI's dump; read back by its round-trip test) ----
[YamlSerializable(typeof(TechTreeDump))]
[YamlSerializable(typeof(FactionAnalysis))]
[YamlSerializable(typeof(StartingState))]
[YamlSerializable(typeof(TechInfo))]
[YamlSerializable(typeof(BuildableInfo))]
public partial class FactionsYamlContext : StaticContext;

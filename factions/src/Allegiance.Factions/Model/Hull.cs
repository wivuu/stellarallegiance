namespace Allegiance.Factions.Model;

/// <summary>
/// A ship chassis. Mirrors the C++ <c>DataHullTypeIGC</c> (igc.h:1767). Sound ids and the
/// trailing hardpoint data from the original struct are intentionally omitted here — model
/// geometry / hardpoints are covered separately (GLB-AND-HARDPOINT-FORMAT.md).
/// </summary>
public record Hull : Buildable
{
    /// <summary>Ship mass; affects acceleration/inertia in the flight model.</summary>
    public double Mass { get; set; }

    /// <summary>
    /// Additive radar-signature bias on top of <c>radar-signature</c> below (radar-signature units,
    /// default 0 = neutral). Projected onto <c>ShipClassDef.SignatureBias</c>; the parts a ship has
    /// equipped add their own <see cref="Part.Signature"/> per ship at runtime.
    /// </summary>
    public double Signature { get; set; }

    /// <summary>Maximum forward flight speed, in world units/second.</summary>
    public double Speed { get; set; }

    /// <summary>Peak yaw/pitch/roll turn rates this hull can reach, in degrees/second.</summary>
    public TurnRates MaxTurnRates { get; set; } = new();

    /// <summary>Legacy Core turn-acceleration stat (not currently used by the flight model); drift-*-deg below governs runtime turn feel instead.</summary>
    public TurnRates TurnTorques { get; set; } = new();

    /// <summary>Forward thrust acceleration.</summary>
    public double Thrust { get; set; }

    /// <summary>Fraction of forward thrust available when strafing sideways (1.0 = full thrust).</summary>
    public double StrafeThrustMultiplier { get; set; } = 1.0;

    /// <summary>Fraction of forward thrust available when reversing (1.0 = full thrust).</summary>
    public double ReverseThrustMultiplier { get; set; } = 1.0;

    /// <summary>Legacy Core sensor-range stat; fog-of-war instead uses the vision-cone/vision-sphere fields below.</summary>
    public double ScannerRange { get; set; }

    /// <summary>
    /// Afterburner fuel tank the equipped afterburner burns (IGC <c>maxFuel</c>, untranslated). A
    /// hull with an afterburner slot must author it (&gt; 0); a hull without one must not (0 = no
    /// fuel model — dead data otherwise). Refilled at dock and by fuel pods from the hold.
    /// </summary>
    public double MaxFuel { get; set; }

    /// <summary>Legacy Core electronic-countermeasures stat (not currently used by the runtime).</summary>
    public double Ecm { get; set; }

    /// <summary>Legacy Core hull length stat; model-length below sizes the runtime GLB instead.</summary>
    public double Length { get; set; }

    /// <summary>
    /// Energy pool (IGC <c>maxEnergy</c>, untranslated) that energy guns (<c>energy-per-shot</c>)
    /// and the cloak draw from; full at launch and refilled at dock. Scaled by the team
    /// <see cref="GameAttribute.MaxEnergy"/> multiplier. 0 = no pool: energy guns can't fire and a
    /// cloak slot is refused. Must be &gt;= 0.
    /// </summary>
    public double MaxEnergy { get; set; }

    /// <summary>Energy regained per second, clamped to the pool (IGC <c>rechargeRate</c>, untranslated). Must be &gt;= 0.</summary>
    public double EnergyRechargeRate { get; set; }

    /// <summary>Legacy Core ripcord (emergency warp) departure speed (not currently used by the runtime).</summary>
    public double RipcordSpeed { get; set; }

    /// <summary>Legacy Core ripcord (emergency warp) money cost (not currently used by the runtime).</summary>
    public double RipcordCost { get; set; }

    /// <summary>
    /// Magazine shared by every ammo-costing gun the ship mounts (<c>ammo-per-shot</c>; IGC
    /// <c>maxAmmo</c>, untranslated). Full at launch, refilled at dock and by ammo packs loaded out
    /// of the hold. 0 = no magazine: ammo guns can't fire and ammo packs are refused as cargo. Must
    /// be in 0..65535 (the runtime carries it as a u16).
    /// </summary>
    public int MaxAmmo { get; set; }

    /// <summary>Hull points before the ship is destroyed.</summary>
    public double ArmorHitPoints { get; set; }

    /// <summary>Legacy Core cap on mountable weapons (not currently enforced by the runtime, which derives loadout from hardpoints).</summary>
    public int MaxWeapons { get; set; }

    /// <summary>Legacy Core cap on fixed (forward-firing) weapons (not currently enforced by the runtime).</summary>
    public int MaxFixedWeapons { get; set; }

    /// <summary>Defense table id used to resolve incoming damage against armor.</summary>
    public string? DefenseType { get; set; }

    /// <summary>Hull upgrade target; references another hull <c>id</c>.</summary>
    public string? SuccessorHullId { get; set; }

    /// <summary>
    /// The suggested default loadout by part id, in IGC order (Allegiance's
    /// <c>preferredPartsTypes</c>). The runtime reads its EQUIPMENT entries: per slot, the default
    /// part is the first entry the slot allows whose required-techs and required-capabilities the
    /// hull itself already requires, so a default is always buildable whenever the hull is (the
    /// rule Allegiance's <c>TryToBuyParts</c> applies, clintlib.h:1341); research tier succession
    /// then upgrades it. No such entry = the slot starts empty. Every shield/afterburner/cloak
    /// listed here must be allowed by <see cref="AllowedParts"/>. Default guns come from the
    /// hardpoints' <c>weapon-id</c> instead.
    /// </summary>
    public List<string> PreferredParts { get; set; } = new();

    /// <summary>
    /// Per-slot whitelist of mountable parts: for each <see cref="EquipmentSlot"/> key (kebab-case,
    /// e.g. <c>shield</c>), the part ids that may be mounted there. Replaces the C++
    /// <c>pmEquipment[ET_MAX]</c> part-mask array. The runtime enforces the EQUIPMENT keys
    /// (<c>shield</c> / <c>afterburner</c> / <c>cloak</c>): a hull HAS an equipment slot only if it
    /// lists that key, and each listed part implicitly allows its whole successor chain (a hull
    /// allowed Sm Shield 1 may carry Sm Shield 2/3 once researched). Every listed part must be of
    /// the slot's kind, and the <c>pack</c> key is refused (packs are cargo). Weapon/launcher keys
    /// are catalog data; gun and rack mounts keep the hardpoint mount-type gate.
    /// </summary>
    public Dictionary<EquipmentSlot, List<string>> AllowedParts { get; set; } = new();

    /// <summary>Capability/role flags this hull carries (e.g. is-fighter, is-lifepod, no-ripcord).</summary>
    public List<HullAbility> Abilities { get; set; } = new();

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// Stable wire class id for this hull as a playable ship (Scout 0 / Fighter 1 / Bomber 2 /
    /// Pod 255). Null = not a runtime-playable hull. The game's <c>ShipClass</c> enum + content id
    /// constants depend on these exact bytes, so they are authored explicitly (not derived).
    /// </summary>
    public byte? ClassId { get; set; }

    /// <summary>
    /// Hangar presentation flavor (projected onto the ShipClassDef; <see cref="Buildable.Description"/>
    /// supplies the blurb). <see cref="Glyph"/> is the UI icon glyph, <see cref="Role"/> the short
    /// role tag (e.g. RECON). Both omit-when-null; empty falls back to a generic client default.
    /// </summary>
    public string? Glyph { get; set; }

    /// <summary>Short hangar role tag (e.g. RECON, INTERCEPT, ASSAULT); empty falls back to a generic client default.</summary>
    public string? Role { get; set; }

    /// <summary>
    /// Longest local axis (world units) the client uniform-scales the loaded hull GLB to (its
    /// silhouette length). Also sizes the engine glow and the loadout preview camera. Projected
    /// onto <c>ShipClassDef.ModelLength</c>.
    /// </summary>
    public double ModelLength { get; set; }

    /// <summary>
    /// Total payload budget the hull can carry: the summed <see cref="Part.Mass"/> of mounted
    /// weapons plus the cargo hold (expendable <see cref="Expendable.Mass"/> × count). 0 = no hold
    /// (e.g. the pod). Runtime hulls with weapon hardpoints must author enough capacity for their
    /// default loadout — <c>CoreValidator</c> enforces this at load.
    /// </summary>
    public double PayloadCapacity { get; set; }

    /// <summary>
    /// Ore hold size (helium-3 units) for a mining hull: the harvest capacity a miner fills at a rock
    /// and offloads at a friendly base. 0 = not a miner (no ore hold). Independent of
    /// <see cref="PayloadCapacity"/> (weapons/cargo budget) — an ore hull is typically unarmed.
    /// Omit-when-default; projected onto <c>ShipClassDef.OreCapacity</c>.
    /// </summary>
    public double OreCapacity { get; set; }

    /// <summary>
    /// Cargo hold: the number of loose salvaged items (one slot per part or per consumable stack)
    /// this hull can carry INERT when it cannot equip them — a gun with no free mount, rounds for a
    /// rack it doesn't fly, a pack past the payload budget. Hold contents cost no payload; they
    /// re-drop on death and are lost on dock. 0 (default) = no hold, the hull ricochets what it can't
    /// use. Independent of <see cref="PayloadCapacity"/>. Omit-when-default; projected onto
    /// <c>ShipClassDef.CargoCapacity</c> (0–255).
    /// </summary>
    public int CargoCapacity { get; set; }

    /// <summary>
    /// Production delay (seconds) between ORDERING this hull and it actually launching — the miner's
    /// analogue of a constructor's Producing phase. When a team buys a miner it is charged + counted
    /// immediately but does not fly until this many seconds elapse (0 = launches at once). Sits next
    /// to <see cref="Buildable.Price"/> as the other faction-tunable cost of a miner. Omit-when-default;
    /// projected onto <c>ShipClassDef.OrderTimeSeconds</c>.
    /// </summary>
    public int OrderTimeSeconds { get; set; }

    /// <summary>Drift (turn-rate slop) knobs the game's flight model needs; no clean Core source.</summary>
    public double DriftYawDeg { get; set; }

    /// <summary>Pitch-axis drift (turn-rate slop) knob, in degrees; pairs with drift-yaw-deg above.</summary>
    public double DriftPitchDeg { get; set; }

    /// <summary>
    /// Afterburner fuel regained per second while the boost is released. 0 = dock-only refill
    /// (Allegiance never refuels in flight; the stock value). Must be &gt;= 0 and below the
    /// fuel-consumption of every afterburner the hull allows — else the gauge never net-drains.
    /// </summary>
    public double AbFuelRecharge { get; set; }

    /// <summary>
    /// Long-range directional sensor cone: <see cref="VisionConeLength"/> is its max range (u),
    /// <see cref="VisionConeAngleDeg"/> its half-angle (degrees); asteroids occlude it. Paired with
    /// an omnidirectional <see cref="VisionSphereRadius"/> proximity sensor (unoccluded, shorter
    /// range). <see cref="RadarSignature"/> is a detection-range multiplier applied to every
    /// viewer's range against THIS hull (0/omitted -&gt; 1.0 at projection; &lt;1 stealthier, &gt;1
    /// easier to spot). All omit-when-default; projected onto the ShipClassDef vision fields.
    /// </summary>
    public double VisionConeLength { get; set; }

    /// <summary>Half-angle of the forward vision cone, in degrees.</summary>
    public double VisionConeAngleDeg { get; set; }

    /// <summary>Radius of the unoccluded omnidirectional proximity sensor, in world units.</summary>
    public double VisionSphereRadius { get; set; }

    /// <summary>Detection-range multiplier applied to every viewer's range against this hull (omitted/0 resolves to 1.0; below 1 is stealthier, above 1 easier to spot).</summary>
    public double RadarSignature { get; set; }

    /// <summary>Local-space mount points (weapon muzzles, engine nozzles, lights) the client renders from.</summary>
    public List<Hardpoint> Hardpoints { get; set; } = new();

    /// <summary>
    /// Default consumable hold this hull spawns with: each entry names an expendable (by id) and a
    /// count. Consumes payload-capacity alongside mounted weapon mass — <c>CoreValidator</c> proves
    /// the summed loadout fits at load. Omit-when-empty. Projected onto <c>ShipClassDef.DefaultCargo</c>
    /// (resolving each expendable id to its cargo-id).
    /// </summary>
    public List<CargoLoad> DefaultCargo { get; set; } = new();

    /// <summary>
    /// Station classes (kebab-case keywords, e.g. <c>shipyard</c>) this hull may LAUNCH from and
    /// DOCK at. Empty/omitted = any friendly base (stock behavior). Non-empty = the hull launches
    /// only from bases whose station <see cref="Station.Class"/> is listed, docks only at such
    /// bases (every other friendly base is solid, like an enemy base), and must use the base's
    /// LARGEST docking door — side doors stay small-ship-only. Omit-when-empty. Projected onto
    /// <c>ShipClassDef.LaunchClassMask</c>.
    /// </summary>
    public List<StationClass> LaunchStationClasses { get; set; } = new();

    // ---- Removed keys (TOMBSTONES) --------------------------------------------------------------
    // The shield and afterburner stats moved from the hull onto the equipment parts (equipment.yaml:
    // shields: / afterburners:). The YAML reader IGNORES unknown keys, so simply deleting these would
    // let a custom bundle that still authors them boot with no shield and no boost, silently. They
    // survive as nullable properties only so CoreValidator can refuse any value; nothing reads them.
    // (Not [Obsolete]: the source-generated YAML context must set them, and would warn on every build.)

    /// <summary>REMOVED — boost moved to the afterburner part's <c>max-thrust</c> (equipment.yaml). Refused when set.</summary>
    public double? AbAccel { get; set; }

    /// <summary>REMOVED — moved to the afterburner part's <c>on-rate</c> (equipment.yaml). Refused when set.</summary>
    public double? AbOnRate { get; set; }

    /// <summary>REMOVED — moved to the afterburner part's <c>off-rate</c> (equipment.yaml). Refused when set.</summary>
    public double? AbOffRate { get; set; }

    /// <summary>REMOVED — moved to the afterburner part's <c>fuel-consumption</c> (equipment.yaml). Refused when set.</summary>
    public double? AbFuelDrain { get; set; }

    /// <summary>REMOVED — moved to the shield part's <c>max-strength</c> (equipment.yaml). Refused when set.</summary>
    public double? ShieldCapacity { get; set; }

    /// <summary>REMOVED — moved to the shield part's <c>regen-rate</c> (equipment.yaml). Refused when set.</summary>
    public double? ShieldRecharge { get; set; }

    /// <summary>REMOVED — moved to the shield part's <c>recharge-delay</c> (equipment.yaml). Refused when set.</summary>
    public double? ShieldDelay { get; set; }
}

/// <summary>One entry in a hull's default consumable hold: an expendable id + a count.</summary>
public record CargoLoad
{
    /// <summary>References an <see cref="Expendable.Id"/> that carries a cargo-id.</summary>
    public string Item { get; set; } = "";

    /// <summary>How many units of that expendable the hull spawns with.</summary>
    public int Count { get; set; }
}

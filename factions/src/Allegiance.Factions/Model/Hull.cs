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
    /// <summary>Base radar cross-section from the original Core model; the fog-of-war vision system instead uses <c>radar-signature</c> below.</summary>
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
    /// <summary>Afterburner fuel tank size; pairs with ab-fuel-drain/ab-fuel-recharge below (0 = no afterburner fuel modeled).</summary>
    public double MaxFuel { get; set; }
    /// <summary>Legacy Core electronic-countermeasures stat (not currently used by the runtime).</summary>
    public double Ecm { get; set; }
    /// <summary>Legacy Core hull length stat; model-length below sizes the runtime GLB instead.</summary>
    public double Length { get; set; }

    /// <summary>Legacy Core energy-pool stat (not currently used by the runtime).</summary>
    public double MaxEnergy { get; set; }
    /// <summary>Legacy Core energy-regen stat (not currently used by the runtime).</summary>
    public double EnergyRechargeRate { get; set; }

    /// <summary>Legacy Core ripcord (emergency warp) departure speed (not currently used by the runtime).</summary>
    public double RipcordSpeed { get; set; }
    /// <summary>Legacy Core ripcord (emergency warp) money cost (not currently used by the runtime).</summary>
    public double RipcordCost { get; set; }

    /// <summary>Legacy Core max carried ammo stat (not currently used by the runtime).</summary>
    public int MaxAmmo { get; set; }
    /// <summary>Hull points before the ship is destroyed.</summary>
    public double ArmorHitPoints { get; set; }

    /// <summary>Legacy Core cap on mountable weapons (not currently enforced by the runtime, which derives loadout from hardpoints).</summary>
    public int MaxWeapons { get; set; }
    /// <summary>Legacy Core cap on fixed (forward-firing) weapons (not currently enforced by the runtime).</summary>
    public int MaxFixedWeapons { get; set; }

    /// <summary>Defense table id used to resolve incoming damage against armor.</summary>
    public string? DefenseType { get; set; }

    /// <summary>Legacy Core magazine (missile rack) capacity stat (not currently used by the runtime).</summary>
    public int MagazineCapacity { get; set; }
    /// <summary>Legacy Core dispenser (mine/chaff pack) capacity stat (not currently used by the runtime).</summary>
    public int DispenserCapacity { get; set; }
    /// <summary>Legacy Core chaff-launcher capacity stat (not currently used by the runtime).</summary>
    public int ChaffLauncherCapacity { get; set; }

    /// <summary>Hull upgrade target; references another hull <c>id</c>.</summary>
    public string? SuccessorHullId { get; set; }

    /// <summary>Suggested default loadout — references part ids.</summary>
    public List<string> PreferredParts { get; set; } = new();

    /// <summary>
    /// Per-slot whitelist of mountable parts: for each <see cref="EquipmentSlot"/>, the part ids
    /// that may be mounted there. Replaces the C++ <c>pmEquipment[ET_MAX]</c> part-mask array.
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

    /// <summary>Afterburner flight knobs (extra accel + spool on/off rates); no clean Core source.</summary>
    public double AbAccel { get; set; }
    /// <summary>How fast the afterburner boost spools up to full extra accel once engaged (per second).</summary>
    public double AbOnRate { get; set; }
    /// <summary>How fast the afterburner boost spools back down once released (per second).</summary>
    public double AbOffRate { get; set; }

    /// <summary>Afterburner fuel drain/recharge (per second); pairs with the Core <see cref="MaxFuel"/> field above.</summary>
    public double AbFuelDrain { get; set; }
    /// <summary>Afterburner fuel regained per second while boost is released (0 = dock-only refill, no in-flight regen).</summary>
    public double AbFuelRecharge { get; set; }

    /// <summary>
    /// Regenerating energy shield layered over the raw hull. <see cref="ShieldCapacity"/> is the
    /// total shield pool (0 = this hull has NO shield). Incoming damage depletes the shield before
    /// the hull and overflows into the hull when the shield pops. <see cref="ShieldRecharge"/> is
    /// the regen rate in points/second, resuming <see cref="ShieldDelay"/> seconds after the last
    /// shield damage. All omit-when-default; projected onto the ShipClassDef shield fields.
    /// </summary>
    public double ShieldCapacity { get; set; }
    /// <summary>Shield regen rate, in points/second, once recharge resumes.</summary>
    public double ShieldRecharge { get; set; }
    /// <summary>Seconds after the last shield hit before recharge resumes.</summary>
    public double ShieldDelay { get; set; }

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
}

/// <summary>One entry in a hull's default consumable hold: an expendable id + a count.</summary>
public record CargoLoad
{
    /// <summary>References an <see cref="Expendable.Id"/> that carries a cargo-id.</summary>
    public string Item { get; set; } = "";

    /// <summary>How many units of that expendable the hull spawns with.</summary>
    public int Count { get; set; }
}

using System.Collections.Generic;
using Godot;
using SpacetimeDB.Types;
using StellarAllegiance.Shared;

// =====================================================================
//  DefRegistry.cs — CLIENT MIRROR OF THE RUNTIME-CONFIGURABLE CONTENT (Phase-1 M3)
//
//  Subscribes to the public def tables seeded/overridable on the server
//  (module/spacetimedb/Defs.cs) and keeps a local dictionary of each. Everything
//  that used to be a compiled-in constant on the client — a hull's flight stats, a
//  gun's speed/spread/fire-rate, a ship/base's hardpoint layout — now flows from
//  here, so an operator's runtime Upsert* retune is reflected with NO client rebuild.
//
//  Determinism: TryGetStats rebuilds the SAME shared ShipStats the server derives
//  (ShipStats.Create from the row's authored f32s), so the client's prediction and
//  the server's authority integrate bit-identically. There is deliberately NO
//  compile-time tuning fallback on the client: a def the client doesn't have yet
//  makes the getter return false and the caller GUARDS (holds authority, doesn't
//  predict) rather than flying stale baked numbers. Def rows ship in the initial
//  subscription snapshot, before any ship can spawn, so that window is momentary.
//
//  The def tables are already in ConnectionManager's SubscribeToAllTables() set; we
//  only register the row callbacks here (via the Connected event, before the
//  snapshot applies — same pattern as WorldRenderer).
// =====================================================================
public partial class DefRegistry : Node
{
	// The pod's reserved ClassId (mirror of module Defs.cs PodClassId). Pods are picked
	// at runtime via Ship.IsPod, not a ShipClass, so their def sits at 255, well clear
	// of the real-hull ids (0,1,2,…) future hull_stats rows will claim.
	public const byte PodClassId = 255;

	private ConnectionManager _cm = null!;

	private readonly Dictionary<byte, ShipClassDef> _ships = new();
	private readonly Dictionary<uint, WeaponDef> _weapons = new();
	private readonly Dictionary<byte, BaseDef> _bases = new();

	// Derived ShipStats memo keyed by the looked-up ClassId. ShipStats.Create runs an
	// Exp() and is too costly to repeat per-ship per-tick, so memoize like the server's
	// _shipStatsCache. Pure function of the row, so it never breaks determinism; the
	// entry is cleared whenever that class's def is inserted/updated/deleted.
	private readonly Dictionary<byte, ShipStats> _statsCache = new();

	public override void _Ready()
	{
		_cm = GetNode<ConnectionManager>("../ConnectionManager");
		_cm.Connected += OnConnected;
	}

	private void OnConnected(DbConnection conn)
	{
		conn.Db.ShipClassDef.OnInsert += (_, r) => { _ships[r.ClassId] = r; _statsCache.Remove(r.ClassId); };
		conn.Db.ShipClassDef.OnUpdate += (_, _, r) => { _ships[r.ClassId] = r; _statsCache.Remove(r.ClassId); };
		conn.Db.ShipClassDef.OnDelete += (_, r) => { _ships.Remove(r.ClassId); _statsCache.Remove(r.ClassId); };

		conn.Db.WeaponDef.OnInsert += (_, r) => _weapons[r.WeaponId] = r;
		conn.Db.WeaponDef.OnUpdate += (_, _, r) => _weapons[r.WeaponId] = r;
		conn.Db.WeaponDef.OnDelete += (_, r) => _weapons.Remove(r.WeaponId);

		conn.Db.BaseDef.OnInsert += (_, r) => _bases[r.BaseTypeId] = r;
		conn.Db.BaseDef.OnUpdate += (_, _, r) => _bases[r.BaseTypeId] = r;
		conn.Db.BaseDef.OnDelete += (_, r) => _bases.Remove(r.BaseTypeId);
	}

	// ---- Ship flight stats ------------------------------------------------

	// Build the shared ShipStats for a class from the def's authored f32s — bit-identical
	// to the server's ShipStatsFor, since both feed ShipStats.Create the same bits. A pod
	// resolves to PodClassId (its IsPod flag, not its ShipClass). False until the row
	// arrives (or for a class with no def) — the caller guards rather than flying baked
	// defaults; once the row lands its OnInsert clears the memo, so the real numbers take
	// over on the next call.
	public bool TryGetStats(byte classId, bool isPod, out ShipStats stats)
	{
		byte defId = isPod ? PodClassId : classId;
		if (_statsCache.TryGetValue(defId, out stats))
			return true;
		if (!_ships.TryGetValue(defId, out var d))
			return false;

		stats = ShipStats.Create(d.MaxSpeed, d.Accel, d.Mass,
			d.RateYawDeg, d.RatePitchDeg, d.RateRollDeg,
			d.DriftYawDeg, d.DriftPitchDeg, d.SideMult, d.BackMult,
			d.AbAccel, d.AbOnRate, d.AbOffRate);
		_statsCache[defId] = stats;
		return true;
	}

	// ---- Weapons / hardpoints --------------------------------------------

	// Resolve a ship class's primary Weapon hardpoint and the WeaponDef it fires (mirror of
	// the server's TryGetWeapon). False when the class has no def, carries no Weapon
	// hardpoint (e.g. a pod), or the named weapon is missing — in every case the ship
	// simply doesn't fire, matching the server so the client never predicts a phantom shot.
	public bool TryGetWeapon(byte classId, out HardpointDef hp, out WeaponDef weapon)
	{
		hp = null!;
		weapon = null!;
		if (!_ships.TryGetValue(classId, out var def) || def.Hardpoints is null)
			return false;
		foreach (var h in def.Hardpoints)
		{
			if (h.Kind != HardpointKind.Weapon)
				continue;
			if (_weapons.TryGetValue(h.WeaponId, out var w))
			{
				hp = h;
				weapon = w;
				return true;
			}
			return false;   // a Weapon hardpoint naming a missing def: don't fire
		}
		return false;       // no Weapon hardpoint on this class
	}

	// A class's full hardpoint list (engines/turrets/lights/docking + weapons), for the
	// M4 ship-mesh loader. Null until the def arrives.
	public List<HardpointDef>? GetHardpoints(byte classId)
		=> _ships.TryGetValue(classId, out var d) ? d.Hardpoints : null;

	public bool TryGetShipDef(byte classId, out ShipClassDef def)
		=> _ships.TryGetValue(classId, out def!);

	public WeaponDef? GetWeapon(uint weaponId)
		=> _weapons.TryGetValue(weaponId, out var w) ? w : null;

	// A base type's def (radius/health/hardpoints), for the M5 base-mesh loader. Null
	// until the row arrives.
	public BaseDef? GetBaseDef(byte baseTypeId)
		=> _bases.TryGetValue(baseTypeId, out var b) ? b : null;
}

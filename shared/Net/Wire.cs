namespace StellarAllegiance.Shared;

// Wire-level constants shared verbatim by the server protocol writer and the Godot client
// reader. Single source of truth: server/Net/Protocol.cs and client GameNetClient alias these
// consts instead of duplicating the values.
public static class Wire
{
    // Wire-format version. Bump whenever a frame layout changes. The client checks this in the
    // Welcome handshake and refuses to play against a skewed server instead of misreading frames.
    // v25: per-sector environment appended to every sector static (Welcome + MsgReveal) —
    // sun/god-rays, nebula override, and the seeded dust-cloud list. See Protocol.WriteSectorEnv.
    // v27: dust block carries an `opacity` float (after the color) — scales both the rendered puff
    // alpha and the radar/vision attenuation, decoupled from the visual `amount`.
    // v28: sun block carries an `ambient` float (after energy) — the sector's ambient/fill light energy.
    // v29: sun block carries a `size` float (after ambient) — the visible disc's world-space quad width
    // (-1 sentinel = client default). See Protocol.WriteSectorEnv / Sun.SetDiscSize.
    // v30: MsgSetAutopilot=11 (client->server engage/disengage) + ShipFlagAutopilot=16 in the ship
    // record flags byte (server-steered autopilot engaged). See server/Net/Protocol.cs.
    // v31: mining — every RockStatic (Welcome + MsgReveal) appends u8 rockClass | f32 currentRadius |
    // u8 orePct (live shrink carried on first sight); new MsgRockUpdate=22 streams live rock shrink
    // deltas; ShipFlagMiner=32 in the ship flags byte; ShipClassDef.OreCapacity added to MsgDefs.
    // v32: miner brain — RockStatic appends f32 OreCapacity as its LAST field (47->51 bytes);
    // ShipFlagMining=64 in the ship flags byte (set while a miner is actively moving ore).
    // v33: MsgMinerTargets=23 (u8 count, count x u64 shipId + u64 rockId) — the exact rock each active
    // miner is harvesting, so the client mining beam aims at the real target instead of guessing.
    // v34: commander — MsgOrder=12 (client->server: u64 subjectShipId, u8 targetKind, u64 targetId,
    // u32 sector, 3x f32 pos); MsgChatRelay scope 2 = commander order directive (gold on the client);
    // MsgLobbyState tail appends i32 commander0 + i32 commander1 after selectedMap.
    // v35: MsgMinefields=13 frame gains a u16 anchor-sector header BEFORE the u8 count
    // ([13][u16 anchorSector][u8 count] + count x 41-B records). Per-record sector is unchanged. The
    // header lets the client prune stale fields even from an empty (count==0) frame, and the server now
    // also emits a frame whenever a client's anchor sector changes (warp) so mines never leak across
    // sectors. See server/Net/ClientHub.BuildMinefieldsFor + client GameNetClient.ApplyMinefields.
    // v36: tech paths — MsgSpawn gains u64 launchBaseId after cls (0 = server default base);
    // MsgDefs appends the tech catalog (u16-counted techs/developments/station-catalog) after the
    // world config, plus BaseDef +u8 researchSlots (after hardpoints) and WeaponDef +TechList
    // requiredTechs (after probeModelSize); MsgTeamState appends per-team owned tech indices +
    // capability bytes after the unlocked-class list; NEW MsgResearch=13 (client->server commander
    // research op: u8 op, u64 baseId, u16 devIndex) + NEW MsgResearchState=24 (server->client
    // per-team per-base research orders, startTick+duration encoded). TechList = u8 n x u16 index
    // into the streamed tech catalog. See Protocol.BuildDefs/BuildTeamState/BuildResearchStateFor.
    // v37: base building — BaseDef appends str ModelName + u8 winCondition + u8 buildRockClass (after
    // researchSlots); StationCatalog appends u8 buildRockClass (after researchSlots); WriteBaseStatic
    // appends u8 baseTypeId and streams the per-type radius (was the World.BaseRadius constant);
    // NEW MsgBuildConstructor=14 (client->server commander: u8 stationTypeId, u64 launchBaseId) +
    // NEW MsgConstructorBuilds=25 (server->client: u8 count, count x (u64 shipId, u64 rockId, u8 phase,
    // f16 progress)); ShipFlagConstructor=128 now emitted (AI constructor drone). See
    // Simulation.Constructors.cs / Protocol.BuildConstructorBuilds.
    // constructor polish — a bought constructor now PRODUCES at the garrison before launching
    // (timed, cancellable). NEW MsgConstructorState=26 (server->client per-team: u8 count, count x
    // (u64 id, u8 stationTypeId, u8 state, u32 startTick, u32 durationTicks, u64 targetId)) drives the
    // Build-tab progress/cancel + drone status; NEW MsgConstructorCancel=15 (client->server commander:
    // u64 constructorId) refunds a producing constructor. Constructors now accept move orders (MsgOrder
    // kinds point/sector). MsgConstructorBuilds=25 now emits a 0-count keepalive briefly after builds
    // end (was null) so the client fades the build sphere. See Simulation.Constructors.cs.
    // a finished constructor base CONSUMES its asteroid — NEW MsgRockGone=27 (server->client
    // broadcast: u8 count, count x u64 rockId) tells clients to delete the despawned rock (node +
    // collision) so nothing remains under the new base. See World.RemoveRock / Protocol.BuildRockGone.
    // v38: constructor build-sequence rework — StationCatalog record appends i32 alignTimeSeconds
    // (after buildRockClass): the per-station constructor align dwell (stations.yaml
    // align-time-seconds). MsgConstructorState `state` bytes renumbered: a new Approaching=5 state
    // (standoff -> surface-contact creep) shifts Sinking to 6 and Building to 7; Sinking/Approaching
    // now stream 0/0 start/duration (distance-gated, untimed). MsgConstructorBuilds phase semantics:
    // phase 1 (sink) begins at surface CONTACT and its progress is the physical embed-depth fraction
    // (was a timer), so the client's build sphere emerges only once the meshes intersect. See
    // Simulation.Constructors.cs / world.yaml `constructor:`.
    // per-ship weapon loadouts (still 38 — nothing released in between; server+client deploy
    // together as usual): MsgSpawn appends an optional mount-override tail after the cargo block
    // ([u8 nMounts] + nMounts x (u8 hpIndex, u32 weaponId); weaponId u32.Max = deliberately-empty
    // slot, unlisted slots keep the authored default); NEW MsgShipLoadout=28 (server->client
    // reliable full table, on change + coarse keepalive: u8 count, count x (u64 shipId, u8 nSlots,
    // nSlots x u32 weaponId) — per-barrel EFFECTIVE weapon ids in hardpoint declaration order,
    // reconcile-by-omission). Guns moved to per-mount cadence; the ship record is UNCHANGED —
    // which mounts fired at LastFireTick is derived client-side via the shared FireCadence rule.
    public const byte ProtocolVersion = 38;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT" — not on a team). It
    // travels on the wire anywhere a team byte does and never indexes a real team array.
    public const byte NoTeam = 0xFF;

    // Max length of a team name (MsgSetTeamName). Enforced on the client's editor + send path and
    // re-clamped server-side; kept here so both agree on where a rename gets truncated.
    public const int TeamNameMaxLength = 20;
}

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
    public const byte ProtocolVersion = 33;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT" — not on a team). It
    // travels on the wire anywhere a team byte does and never indexes a real team array.
    public const byte NoTeam = 0xFF;

    // Max length of a team name (MsgSetTeamName). Enforced on the client's editor + send path and
    // re-clamped server-side; kept here so both agree on where a rename gets truncated.
    public const int TeamNameMaxLength = 20;
}

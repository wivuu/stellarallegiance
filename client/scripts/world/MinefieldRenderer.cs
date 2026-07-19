using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// The chaff + minefield stream (MsgChaff / MsgMinefields / MsgMineGone / minefield reconcile). Wraps the
// two ChaffFx / MinefieldViews child-node systems (Track A/B visuals) behind one seam that GameNetClient
// and the HUD talk to. Needs the live server tick for minefield-upsert timing.
public sealed class MinefieldRenderer
{
    private readonly ChaffFx _chaff;
    private readonly MinefieldViews _minefields;
    private readonly DefRegistry _defs;
    private readonly MatchClock _clock;

    public MinefieldRenderer(ChaffFx chaff, MinefieldViews minefields, DefRegistry defs, MatchClock clock)
    {
        _chaff = chaff;
        _minefields = minefields;
        _defs = defs;
        _clock = clock;
    }

    public void NetSpawnChaff(ulong id, byte team, uint sector, Vec3 pos, Vec3 vel, uint weaponId) =>
        _chaff.Spawn(
            id,
            team,
            sector,
            new Vector3(pos.X, pos.Y, pos.Z),
            new Vector3(vel.X, vel.Y, vel.Z),
            _defs.GetWeapon(weaponId)
        );

    public void NetUpsertMinefield(Minefield row) =>
        _minefields.Upsert(row, _defs.GetWeapon(row.WeaponId), _clock.ServerTick);

    public void NetMineGone(ulong fieldId, byte mineIndex, byte reason, uint sector, Vec3 pos) =>
        _minefields.MineGone(fieldId, mineIndex, reason, sector, new Vector3(pos.X, pos.Y, pos.Z));

    // Free a minefield's cloud on client-cache reconcile (GameNetClient.ApplyMinefields drops any field
    // the authoritative frame no longer lists — expiry, clear, or a sector we warped out of).
    public void NetMinefieldGone(ulong fieldId) => _minefields.Remove(fieldId);

    // Pass-through to the minefield feed for the HUD mine glyph (mirrors ProbeRenderer.Visible()).
    public IReadOnlyList<(Vector3 Pos, byte Team)> VisibleMinefields() => _minefields.VisibleMinefields();

    // Drop transient chaff/minefield visuals — on a lobby transition (NetSetMatch) and world rebuild
    // (Reset), so a stale hazard from the finished match doesn't linger into the next one.
    public void Clear()
    {
        _chaff.Clear();
        _minefields.Clear();
    }
}

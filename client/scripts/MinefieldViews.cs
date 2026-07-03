using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// =====================================================================
//  MinefieldViews.cs — CLIENT MINEFIELD VISUALS (Track-0 skeleton)
//
//  One container node under WorldRenderer that owns the live minefield sprite clouds. GameNetClient
//  decodes MsgMinefields/MsgMineGone and WorldRenderer.NetUpsertMinefield/NetMineGone forward here.
//  TRACK B fills the body: per-field Node3D of team-tinted "◈" billboard sprites placed from the
//  shared MinefieldLayout.Positions(seed, ...) + wire center, reconciled against aliveMask, pulsed
//  once when armAtTick <= serverTick; MineGone pops a sprite with a CreateBlast + Explosion SFX;
//  fields freed on expiry / Reset / phase→Lobby. Track-0 keeps these no-ops so the client compiles +
//  plays identically until Track B lands.
// =====================================================================
public partial class MinefieldViews : Node3D
{
    // Reconcile/insert a field's sprite cloud from its seed + aliveMask. TRACK B: implement.
    public void Upsert(Minefield row, WeaponDef? def, uint serverTick)
    {
        // Track B: implement.
    }

    // A single mine popped: pop the sprite + play FX at `pos`. TRACK B: implement.
    public void MineGone(ulong fieldId, byte mineIndex, byte reason, Vector3 pos)
    {
        // Track B: implement.
    }

    // Free every field (WorldRenderer Reset / phase→Lobby). Track-0 stub: nothing to free.
    public void Clear()
    {
        // Track B: free children.
    }
}

using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  ChaffFx.cs — CLIENT CHAFF-PUFF VISUALS (Track-0 skeleton)
//
//  One container node under WorldRenderer that owns the live chaff-puff sprites. GameNetClient
//  decodes MsgChaff and WorldRenderer.NetSpawnChaff forwards here. TRACK A fills the body: a
//  team-tinted billboard puff that drifts with the wire velocity and fades over the chaff
//  WeaponDef's ProjectileLifeTicks (there is no gone-message — D2). Track-0 keeps these no-ops so
//  the client compiles + plays identically until Track A lands.
// =====================================================================
public partial class ChaffFx : Node3D
{
    // Spawn a chaff puff visual. TRACK A: billboard sprite drifting with `vel`, fading over the
    // def's lifespan; tag its sector for RefreshSectorVisibility. Track-0 stub: no-op.
    public void Spawn(ulong id, byte team, uint sector, Vector3 pos, Vector3 vel, WeaponDef? def)
    {
        // Track A: implement.
    }

    // Free every live puff (WorldRenderer Reset / phase→Lobby). Track-0 stub: nothing to free.
    public void Clear()
    {
        // Track A: free children.
    }
}

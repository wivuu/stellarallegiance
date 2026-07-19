// The local pilot's team identity — a tiny shared holder so every concern reads one source. `LocalTeam`
// is set when our ship spawns (null until then, e.g. lobby / F3 peek); `LobbyTeam` is the side picked in
// the roster (pushed by GameNetClient.ApplyLobbyState). `MarkerTeam` is the friend/foe classifier for the
// HUD — the spawned team once flying, else the lobby side so the pre-launch peek still marks the
// garrison's ships. Plain holder, no Godot dependency; written by the ship renderer + the lobby seam.
public sealed class PlayerContext
{
    public byte? LocalTeam;
    public byte? LobbyTeam;

    public byte? MarkerTeam => LocalTeam ?? LobbyTeam;
}

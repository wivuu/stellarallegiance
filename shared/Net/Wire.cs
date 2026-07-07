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
    public const byte ProtocolVersion = 26;

    // Sentinel team byte for a pilot who hasn't picked a side ("NOAT" — not on a team). It
    // travels on the wire anywhere a team byte does and never indexes a real team array.
    public const byte NoTeam = 0xFF;

    // Max length of a team name (MsgSetTeamName). Enforced on the client's editor + send path and
    // re-clamped server-side; kept here so both agree on where a rename gets truncated.
    public const int TeamNameMaxLength = 20;
}

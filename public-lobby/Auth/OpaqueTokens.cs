using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace PublicLobby.Auth;

// Lobby-session tokens (plan §1.1, the FIRST token family: player and game-server logins — not
// join tokens). Both are opaque random strings; only their SHA-256 is stored. Shape:
//     "<a|r>.<lineageId:N>.<256-bit base64url random>"
// The lineage id is embedded so a presented token routes straight to its SessionGrain (keyed by
// lineage) without a table scan; it is not a secret and grants nothing on its own.
public static class OpaqueTokens
{
    public const string AccessPrefix = "a";
    public const string RefreshPrefix = "r";

    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(90); // sliding, per rotation

    public static string Mint(string prefix, Guid lineageId) =>
        $"{prefix}.{lineageId:N}.{Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))}";

    // Structural parse only (prefix + lineage). Validity is decided by the grain via the hash.
    public static bool TryParse(string? token, out string prefix, out Guid lineageId)
    {
        prefix = "";
        lineageId = default;
        if (string.IsNullOrEmpty(token) || token.Length > 128)
            return false;
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] is not (AccessPrefix or RefreshPrefix) || parts[2].Length < 32)
            return false;
        if (!Guid.TryParseExact(parts[1], "N", out lineageId))
            return false;
        prefix = parts[0];
        return true;
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

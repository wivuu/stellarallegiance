namespace StellarAllegiance.Shared;

// Join-token derivation shared by the STDB module (mints a token per player when a match
// goes Active) and the native sim server (validates the token offline — no STDB
// connection needed on the hot path). The client never computes tokens; it just relays
// the row it was shown (RLS limits each player to their own row).
//
// DEV-GRADE: FNV-1a-64 keyed by a shared secret. Deterministic and dependency-free so it
// runs inside the wasi module (System.Security.Cryptography is unavailable there).
// Replace with HMAC-SHA256 at the same call sites when the module gains crypto support;
// until then it deters casual spoofing, not a motivated attacker.
public static class JoinTokens
{
    public static string Compute(string secret, string identityHex, byte team)
    {
        ulong h = 14695981039346656037UL;
        Mix(ref h, secret);
        h ^= 0x7C; h *= 1099511628211UL;
        Mix(ref h, identityHex);
        h ^= team; h *= 1099511628211UL;
        return h.ToString("x16");
    }

    private static void Mix(ref ulong h, string s)
    {
        foreach (char c in s)
        {
            h ^= (byte)c; h *= 1099511628211UL;
            h ^= (byte)(c >> 8); h *= 1099511628211UL;
        }
    }
}

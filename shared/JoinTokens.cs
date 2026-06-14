using System;
using System.Text;

namespace StellarAllegiance.Shared;

// Join-token derivation shared by the STDB module (mints a token per player when a match
// goes Active) and the native sim server (validates the token offline — no STDB connection
// on the hot path). The client never computes a token; it relays the row it was shown (RLS
// limits each player to their own JoinToken row).
//
// Construction: HMAC-SHA256 over a canonical message binding the player's identity, team,
// the match epoch, and an absolute expiry. The MAC is keyed by a shared secret (>=32 random
// bytes, see SIM_SECRET / set_sim_endpoint). Properties this gives us over the old 64-bit
// FNV keyed hash:
//   • forgery-resistant: 256-bit MAC, no length-extension/structure to exploit.
//   • no cross-match replay: the epoch is bumped each match, so last match's token won't
//     validate against this match's pinned epoch on the server.
//   • bounded lifetime: the server rejects an expired token against its own wall clock.
// The HMAC/SHA-256 are pure-managed (shared/Hmac.cs) so the wasi module and the native
// server produce byte-identical tokens. The server compares with ConstantTimeEquals.
public static class JoinTokens
{
    // Canonical signed message: identity | team | matchEpoch | expiryUnixSeconds.
    // Pipe-delimited decimal so the fields can't ambiguously run together.
    public static string Compute(string secret, string identityHex, byte team, ulong matchEpoch, long expiryUnix)
    {
        string msg = $"{identityHex}|{team}|{matchEpoch}|{expiryUnix}";
        return HmacSha256.ComputeHex(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(msg));
    }

    // Length-independent, content constant-time string compare for MAC validation, so a
    // network attacker can't byte-probe the expected token via response timing.
    public static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null)
            return false;
        int diff = a.Length ^ b.Length;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

using System;

namespace StellarAllegiance.Shared;

// Constant-time string comparison used by the shared-secret connect authenticator
// (server/Backend/Backends.cs SharedSecretAuthenticator) so response timing can't leak the
// expected password.
//
// This file used to also hold the STDB-era HMAC join-token derivation. Join tokens are now
// short-lived, single-use ES256 JWTs minted by the public lobby and verified offline by the
// game server against the lobby's JWKS — see public-lobby/CONTEXT.md ("Join Token").
public static class JoinTokens
{
    // Length-independent, content constant-time string compare, so a network attacker can't
    // byte-probe the expected secret via response timing.
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

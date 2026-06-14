using System.Text;
using StellarAllegiance.Shared;

// Known-answer + behavioural tests for the pure-managed crypto that backs join tokens.
// Console exe (matches FlightModelTest): exits non-zero on the first failure so CI / the
// build scripts can gate on it. Vectors: FIPS 180-4 (SHA-256) and RFC 4231 (HMAC-SHA256).

static class Program
{
    static int _failures;

    static int Main()
    {
        // ---- SHA-256 KATs (FIPS 180-4 examples) ----
        Eq("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Hex(Sha256.Hash(Encoding.ASCII.GetBytes("abc"))), "SHA256(\"abc\")");
        Eq("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Hex(Sha256.Hash(Array.Empty<byte>())), "SHA256(\"\")");
        // Multi-block (message spans the 56-byte padding boundary).
        Eq("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
            Hex(Sha256.Hash(Encoding.ASCII.GetBytes(
                "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"))),
            "SHA256(56-byte)");

        // ---- HMAC-SHA256 KATs (RFC 4231) ----
        Eq("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
            HmacSha256.ComputeHex(Repeat(0x0b, 20), Encoding.ASCII.GetBytes("Hi There")),
            "RFC4231 case 1");
        Eq("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
            HmacSha256.ComputeHex(Encoding.ASCII.GetBytes("Jefe"),
                Encoding.ASCII.GetBytes("what do ya want for nothing?")),
            "RFC4231 case 2");
        // Case 6: key longer than the 64-byte block (forces the key-hashing path).
        Eq("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54",
            HmacSha256.ComputeHex(Repeat(0xaa, 131),
                Encoding.ASCII.GetBytes(
                    "Test Using Larger Than Block-Size Key - Hash Key First")),
            "RFC4231 case 6 (long key)");

        // ---- Join-token behaviour ----
        const string secret = "test-secret-0123456789";
        string id = "0xfeedface";
        string t1 = JoinTokens.Compute(secret, id, 0, 7, 1_900_000_000);
        string t1b = JoinTokens.Compute(secret, id, 0, 7, 1_900_000_000);
        Check(t1 == t1b, "token is deterministic");
        Check(t1.Length == 64, "token is 64 hex chars (256-bit)");
        Check(t1 != JoinTokens.Compute(secret, id, 1, 7, 1_900_000_000), "team is bound");
        Check(t1 != JoinTokens.Compute(secret, id, 0, 8, 1_900_000_000), "epoch is bound");
        Check(t1 != JoinTokens.Compute(secret, id, 0, 7, 1_900_000_001), "expiry is bound");
        Check(t1 != JoinTokens.Compute("other-secret-9876543210", id, 0, 7, 1_900_000_000),
            "secret is bound");
        Check(JoinTokens.ConstantTimeEquals(t1, t1b), "constant-time equal accepts match");
        Check(!JoinTokens.ConstantTimeEquals(t1, t1[..^1] + "0"), "constant-time rejects diff");
        Check(!JoinTokens.ConstantTimeEquals(t1, t1 + "0"), "constant-time rejects length diff");

        if (_failures == 0)
            Console.WriteLine("CryptoTest: all checks passed");
        else
            Console.WriteLine($"CryptoTest: {_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    static byte[] Repeat(byte b, int n)
    {
        var a = new byte[n];
        Array.Fill(a, b);
        return a;
    }

    static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    static void Eq(string expected, string actual, string name)
    {
        if (expected == actual) { Console.WriteLine($"  ok   {name}"); return; }
        Console.WriteLine($"  FAIL {name}\n       expected {expected}\n       actual   {actual}");
        _failures++;
    }

    static void Check(bool cond, string name)
    {
        if (cond) { Console.WriteLine($"  ok   {name}"); return; }
        Console.WriteLine($"  FAIL {name}");
        _failures++;
    }
}

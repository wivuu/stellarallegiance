using System;

namespace StellarAllegiance.Shared;

// Pure-managed SHA-256 + HMAC-SHA256. Deliberately NO System.Security.Cryptography:
// this same source compiles into the wasi-wasm SpacetimeDB module (which historically
// could not rely on the platform crypto provider at runtime) AND into the native sim
// server, so a join token computed in the module is byte-identical to the one the server
// recomputes. Standard FIPS 180-4 (SHA-256) and RFC 2104 (HMAC); verified against the
// RFC 4231 known-answer vectors in tests/. Not performance-critical — tokens are minted
// once per match start and validated once per socket connect.
public static class Sha256
{
    private static readonly uint[] K =
    {
        0x428a2f98,
        0x71374491,
        0xb5c0fbcf,
        0xe9b5dba5,
        0x3956c25b,
        0x59f111f1,
        0x923f82a4,
        0xab1c5ed5,
        0xd807aa98,
        0x12835b01,
        0x243185be,
        0x550c7dc3,
        0x72be5d74,
        0x80deb1fe,
        0x9bdc06a7,
        0xc19bf174,
        0xe49b69c1,
        0xefbe4786,
        0x0fc19dc6,
        0x240ca1cc,
        0x2de92c6f,
        0x4a7484aa,
        0x5cb0a9dc,
        0x76f988da,
        0x983e5152,
        0xa831c66d,
        0xb00327c8,
        0xbf597fc7,
        0xc6e00bf3,
        0xd5a79147,
        0x06ca6351,
        0x14292967,
        0x27b70a85,
        0x2e1b2138,
        0x4d2c6dfc,
        0x53380d13,
        0x650a7354,
        0x766a0abb,
        0x81c2c92e,
        0x92722c85,
        0xa2bfe8a1,
        0xa81a664b,
        0xc24b8b70,
        0xc76c51a3,
        0xd192e819,
        0xd6990624,
        0xf40e3585,
        0x106aa070,
        0x19a4c116,
        0x1e376c08,
        0x2748774c,
        0x34b0bcb5,
        0x391c0cb3,
        0x4ed8aa4a,
        0x5b9cca4f,
        0x682e6ff3,
        0x748f82ee,
        0x78a5636f,
        0x84c87814,
        0x8cc70208,
        0x90befffa,
        0xa4506ceb,
        0xbef9a3f7,
        0xc67178f2,
    };

    public const int BlockSize = 64; // 512-bit message block (also HMAC key-pad width)
    public const int HashSize = 32; // 256-bit digest

    public static byte[] Hash(byte[] message)
    {
        uint h0 = 0x6a09e667,
            h1 = 0xbb67ae85,
            h2 = 0x3c6ef372,
            h3 = 0xa54ff53a;
        uint h4 = 0x510e527f,
            h5 = 0x9b05688c,
            h6 = 0x1f83d9ab,
            h7 = 0x5be0cd19;

        // Pre-processing: append 0x80, pad with zeros to 56 mod 64, then 64-bit bit length.
        long bitLen = (long)message.Length * 8;
        int padded = ((message.Length + 8) / BlockSize + 1) * BlockSize;
        var data = new byte[padded];
        Array.Copy(message, data, message.Length);
        data[message.Length] = 0x80;
        for (int i = 0; i < 8; i++)
            data[padded - 1 - i] = (byte)(bitLen >> (8 * i));

        var w = new uint[64];
        for (int chunk = 0; chunk < padded; chunk += BlockSize)
        {
            for (int i = 0; i < 16; i++)
                w[i] = (uint)(
                    (data[chunk + 4 * i] << 24)
                    | (data[chunk + 4 * i + 1] << 16)
                    | (data[chunk + 4 * i + 2] << 8)
                    | data[chunk + 4 * i + 3]
                );
            for (int i = 16; i < 64; i++)
            {
                uint s0 = Ror(w[i - 15], 7) ^ Ror(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = Ror(w[i - 2], 17) ^ Ror(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }

            uint a = h0,
                b = h1,
                c = h2,
                d = h3,
                e = h4,
                f = h5,
                g = h6,
                h = h7;
            for (int i = 0; i < 64; i++)
            {
                uint s1 = Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = h + s1 + ch + K[i] + w[i];
                uint s0 = Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = s0 + maj;
                h = g;
                g = f;
                f = e;
                e = d + t1;
                d = c;
                c = b;
                b = a;
                a = t1 + t2;
            }

            h0 += a;
            h1 += b;
            h2 += c;
            h3 += d;
            h4 += e;
            h5 += f;
            h6 += g;
            h7 += h;
        }

        var outp = new byte[HashSize];
        WriteBE(outp, 0, h0);
        WriteBE(outp, 4, h1);
        WriteBE(outp, 8, h2);
        WriteBE(outp, 12, h3);
        WriteBE(outp, 16, h4);
        WriteBE(outp, 20, h5);
        WriteBE(outp, 24, h6);
        WriteBE(outp, 28, h7);
        return outp;
    }

    private static uint Ror(uint x, int n) => (x >> n) | (x << (32 - n));

    private static void WriteBE(byte[] dst, int o, uint v)
    {
        dst[o] = (byte)(v >> 24);
        dst[o + 1] = (byte)(v >> 16);
        dst[o + 2] = (byte)(v >> 8);
        dst[o + 3] = (byte)v;
    }
}

public static class HmacSha256
{
    // RFC 2104 HMAC-SHA256. Returns the 32-byte MAC of `message` under `key`.
    public static byte[] Compute(byte[] key, byte[] message)
    {
        // Keys longer than the block size are hashed down first.
        if (key.Length > Sha256.BlockSize)
            key = Sha256.Hash(key);

        var ipad = new byte[Sha256.BlockSize];
        var opad = new byte[Sha256.BlockSize];
        for (int i = 0; i < Sha256.BlockSize; i++)
        {
            byte k = i < key.Length ? key[i] : (byte)0;
            ipad[i] = (byte)(k ^ 0x36);
            opad[i] = (byte)(k ^ 0x5c);
        }

        var inner = new byte[Sha256.BlockSize + message.Length];
        Array.Copy(ipad, inner, Sha256.BlockSize);
        Array.Copy(message, 0, inner, Sha256.BlockSize, message.Length);
        byte[] innerHash = Sha256.Hash(inner);

        var outer = new byte[Sha256.BlockSize + Sha256.HashSize];
        Array.Copy(opad, outer, Sha256.BlockSize);
        Array.Copy(innerHash, 0, outer, Sha256.BlockSize, Sha256.HashSize);
        return Sha256.Hash(outer);
    }

    // Lowercase hex of the MAC — the form transported in the JoinToken row / Hello frame.
    public static string ComputeHex(byte[] key, byte[] message)
    {
        byte[] mac = Compute(key, message);
        var sb = new System.Text.StringBuilder(mac.Length * 2);
        foreach (byte x in mac)
            sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
}

using StellarAllegiance.Shared.Net;

// HelloMessage (proto 38 layout, generated codec): the Hello reader accepts every older layout —
// every field is [WireOptional], so a shorter frame parses with the missing fields "" and a torn
// tail is treated as absent, never as a rejected frame — and reads the u16 join-token tail.
static class HelloFrameTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool cond, string what)
        {
            Console.WriteLine((cond ? "PASS: " : "FAIL: ") + what);
            if (!cond)
                failures++;
        }

        static HelloMessage Parse(byte[] frame)
        {
            bool ok = HelloMessage.TryParse(frame, out var m);
            if (!ok)
                throw new InvalidOperationException("Hello must never reject a frame");
            return m;
        }

        static byte[] Build(string secret, string name, string token, string join) =>
            new HelloMessage
            {
                Secret = secret,
                Name = name,
                ReconnectToken = token,
                JoinToken = join,
            }.ToBytes();

        var jwt = new string('x', 380);
        var full = Parse(Build("pw", "Vex", "RECON", jwt));
        Check(
            full.Secret == "pw" && full.Name == "Vex" && full.ReconnectToken == "RECON" && full.JoinToken == jwt,
            "full proto-38 frame round-trips (380 B token)"
        );

        var noJoin = Parse(Build("", "Vex", "", ""));
        Check(
            noJoin.Name == "Vex" && noJoin.JoinToken == "" && noJoin.ReconnectToken == "",
            "empty join token parses as \"\""
        );

        // v9 client: no u16 tail at all.
        var v9 = new byte[] { 1, 0, 3, (byte)'V', (byte)'e', (byte)'x', 0 };
        var p9 = Parse(v9);
        Check(
            p9.Name == "Vex" && p9.JoinToken == "" && p9.Secret == "",
            "v9 frame without the tail parses (join token \"\")"
        );

        // v7 simbot: secretLen=0, nameLen=0, nothing else.
        var p7 = Parse(new byte[] { 1, 0, 0 });
        Check(
            p7.Secret == "" && p7.Name == "" && p7.ReconnectToken == "" && p7.JoinToken == "",
            "minimal v7 frame parses to defaults"
        );

        Check(Parse(new byte[] { 1 }).Name == "", "bare Hello byte parses to defaults");

        // Truncated tail: u16 says 10 bytes but only 3 follow → token ignored, earlier fields kept.
        var truncated = Build("", "Vex", "R", "abcdefghij")[..^7];
        var pt = Parse(truncated);
        Check(
            pt.Name == "Vex" && pt.ReconnectToken == "R" && pt.JoinToken == "",
            "truncated join token is ignored, earlier fields survive"
        );

        // Length byte overruns the frame → stop there.
        var overrun = new byte[] { 1, 9, (byte)'a' };
        Check(Parse(overrun).Secret == "", "secret length beyond the frame yields \"\"");

        var unicode = Parse(Build("", "Vëx 🚀", "", ""));
        Check(unicode.Name == "Vëx 🚀", "UTF-8 name round-trips");

        // The hub's rule for an oversized join token (HelloMessage.MaxJoinTokenBytes) is applied by the
        // hub after parsing; the codec itself carries any u16 length.
        Check(HelloMessage.MaxJoinTokenBytes == 4096, "join-token cap constant is 4096");

        return failures;
    }
}

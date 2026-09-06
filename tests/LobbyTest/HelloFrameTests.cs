using SimServer.Net;

// HelloFrame (WP2.2, proto 38): the Hello parser accepts every older layout (fields optional,
// never rejects) and reads the new u16 join-token tail.
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

        var jwt = new string('x', 380);
        var full = HelloFrame.Parse(HelloFrame.Build("pw", "Vex", "RECON", jwt));
        Check(
            full.Secret == "pw" && full.Name == "Vex" && full.ReconnectToken == "RECON" && full.JoinToken == jwt,
            "full proto-38 frame round-trips (380 B token)"
        );

        var noJoin = HelloFrame.Parse(HelloFrame.Build("", "Vex", "", ""));
        Check(
            noJoin.Name == "Vex" && noJoin.JoinToken == "" && noJoin.ReconnectToken == "",
            "empty join token parses as \"\""
        );

        // v9 client: no u16 tail at all.
        var v9 = new byte[] { 1, 0, 3, (byte)'V', (byte)'e', (byte)'x', 0 };
        var p9 = HelloFrame.Parse(v9);
        Check(
            p9.Name == "Vex" && p9.JoinToken == "" && p9.Secret == "",
            "v9 frame without the tail parses (join token \"\")"
        );

        // v7 simbot: secretLen=0, nameLen=0, nothing else.
        var p7 = HelloFrame.Parse(new byte[] { 1, 0, 0 });
        Check(
            p7.Secret == "" && p7.Name == "" && p7.ReconnectToken == "" && p7.JoinToken == "",
            "minimal v7 frame parses to defaults"
        );

        Check(HelloFrame.Parse(new byte[] { 1 }).Name == "", "bare Hello byte parses to defaults");

        // Truncated tail: u16 says 10 bytes but only 3 follow → token ignored, earlier fields kept.
        var truncated = HelloFrame.Build("", "Vex", "R", "abcdefghij")[..^7];
        var pt = HelloFrame.Parse(truncated);
        Check(
            pt.Name == "Vex" && pt.ReconnectToken == "R" && pt.JoinToken == "",
            "truncated join token is ignored, earlier fields survive"
        );

        // Length byte overruns the frame → stop there.
        var overrun = new byte[] { 1, 9, (byte)'a' };
        Check(HelloFrame.Parse(overrun).Secret == "", "secret length beyond the frame yields \"\"");

        var unicode = HelloFrame.Parse(HelloFrame.Build("", "Vëx 🚀", "", ""));
        Check(unicode.Name == "Vëx 🚀", "UTF-8 name round-trips");

        return failures;
    }
}

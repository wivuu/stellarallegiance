namespace SimServer.Update;

// What the update coordinator needs from the game server's front door (ClientHub implements it in
// ClientHub.Update.cs). Narrow on purpose: the coordinator decides WHEN, the hub owns HOW - and the
// tests drive the coordinator against a fake one.
public interface IUpdateGate
{
    // Players currently admitted (past Hello). Bots are connections too and count.
    int ConnectionCount { get; }

    // Ever-increasing count of admissions. Sampling ConnectionCount once a second would miss a pilot who
    // joined and left in between; this does not, and either one resets the "empty since" clock.
    long AdmittedTotal { get; }

    // True once the sim has nothing left to lose: no match running, no ships, nothing to report.
    bool IsQuiescent { get; }

    // Atomically: "nobody is connected" AND "from now on nobody gets in". False = somebody is
    // connected (nothing changed). While draining, a Hello is refused with RejectMessage.CodeUpdating.
    bool TryBeginDrain();

    void EndDrain();

    // The standing Server Notice every connected client (and every later joiner) is shown.
    void SetServerNotice(byte kind, string version);

    // One "★" system chat line to everyone - the pilots in flight never see the lobby banner.
    void AnnounceSystem(string text);
}

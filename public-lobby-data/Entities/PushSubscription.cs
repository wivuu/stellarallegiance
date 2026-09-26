namespace PublicLobby.Data.Entities;

// A Push Subscription (public-lobby/CONTEXT.md): one browser a player turned notifications on in —
// the PushManager subscription's endpoint and keys, exactly as the browser handed them over.
// Delivery is per browser; WHICH events reach it is per account (NotificationPreference), so
// turning one browser off never touches the others. Endpoint is unique: a browser belongs to
// whichever account turned it on last (a shared machine that switches accounts moves the row).
public class PushSubscription
{
    public Guid Id { get; init; }

    public Guid PlayerId { get; set; }

    // The push service URL (FCM / Mozilla autopush / WNS / Apple) the lobby POSTs messages to. Only
    // known push-service hosts are accepted (PushSubscriptions.TryValidate) — the lobby would
    // otherwise POST to any URL a signed-in user hands it.
    public required string Endpoint { get; init; }

    // The browser's P-256 ECDH public key and auth secret (RFC 8291), base64url as the browser
    // encodes them; a re-subscribe of the same endpoint may rotate them.
    public required string P256dh { get; set; }
    public required string Auth { get; set; }

    // "Chrome · macOS", "Safari · iPhone Home Screen" — from the User-Agent at subscribe time, only
    // so /me can tell the rows apart.
    public required string Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
}

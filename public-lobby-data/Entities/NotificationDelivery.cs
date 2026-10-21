namespace PublicLobby.Data.Entities;

// The last time an account was actually sent one Notification Event (public-lobby/CONTEXT.md:
// Notification Cap) - at least one of its browsers took the message. The dispatcher skips an account
// whose row is younger than the cap (Notifications/PushNotifier.cs), so a busy evening of ranked
// matches is one alert, not ten. Test messages from /me never touch it. One row per (player, event),
// overwritten on each send.
public class NotificationDelivery
{
    public Guid PlayerId { get; init; }
    public NotificationEvent Event { get; init; }

    public DateTimeOffset LastSentAt { get; set; }
}

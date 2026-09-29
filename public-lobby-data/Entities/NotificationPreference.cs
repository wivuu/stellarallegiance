namespace PublicLobby.Data.Entities;

// A Notification Preference (public-lobby/CONTEXT.md): one account's explicit choice for one
// Notification Event. A row exists only once the pilot has flipped that event's switch (or pressed
// "Mute these" on a notification); a missing row means the event's default
// (NotificationEvents.DefaultEnabled), so turning a browser on without touching a switch already
// gets the events that default on.
public class NotificationPreference
{
    public Guid PlayerId { get; init; }
    public NotificationEvent Event { get; init; }

    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

using PublicLobby.Data;

namespace PublicLobby.Notifications;

// Web Push notifications (issue #98; public-lobby/README.md "Notifications"). Event choices are per
// account (notification_preferences), delivery is per browser (push_subscriptions); /me and the home
// prompt drive them through Pages/Shared/_Notifications.cshtml + wwwroot/push.js, and wwwroot/sw.js
// shows what arrives. The only event today is ranked.match-started, raised in Api/MatchEndpoints.cs.
static class NotificationsHosting
{
    public static IHostApplicationBuilder AddLobbyNotifications(this IHostApplicationBuilder b)
    {
        // Read once at boot; a malformed key pair throws here so the deploy fails instead of the
        // first send.
        b.Services.AddSingleton(PushOptions.FromEnv(Environment.GetEnvironmentVariable));
        b.Services.AddSingleton<PushSubscriptions>();
        b.Services.AddSingleton<IPushSender, WebPushSender>();
        b.Services.AddSingleton<PushNotifier>();
        b.Services.AddSingleton<PushOutbox>();
        b.Services.AddHostedService<PushDispatcher>();
        return b;
    }

    public static void MapPushApi(this WebApplication app)
    {
        // "Mute these" from a delivered notification (wwwroot/sw.js). Anonymous on purpose: the
        // service worker has no session of its own, and the subscription endpoint it sends is an
        // unguessable capability only that browser and this lobby hold.
        app.MapPost(
            "/push/mute",
            async (PushMuteRequest req, PushOptions options, PushSubscriptions subscriptions) =>
            {
                if (!options.Enabled)
                    return Results.NotFound();
                if (string.IsNullOrWhiteSpace(req.Endpoint) || !NotificationEvents.TryParse(req.Event, out var evt))
                    return Results.BadRequest(new { error = "endpoint and a known event are required" });
                return await subscriptions.MuteAsync(req.Endpoint, evt) ? Results.NoContent() : Results.NotFound();
            }
        );
    }
}

public sealed record PushMuteRequest(string? Endpoint, string? Event);

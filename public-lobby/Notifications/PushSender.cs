using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using PushSubscription = PublicLobby.Data.Entities.PushSubscription;
using WebPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace PublicLobby.Notifications;

public enum PushSendOutcome
{
    Delivered,

    // 404/410 from the push service: the browser dropped this subscription for good; prune the row.
    Gone,

    Failed,
}

// What one notification says - serialized to the JSON wwwroot/sw.js reads in its `push` handler.
// Tag collapses re-sends on the device (the ranked alert uses the match id); Event is the Notification
// Event key the "Mute these" action turns off, null for a message with no such action (the test).
public sealed record PushPayload(string Title, string Body, string Tag, string Url, string? Event)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);
}

// The seam between "who gets what" (PushNotifier) and the Web Push protocol, so the suite can record
// sends instead of reaching a real push service.
public interface IPushSender
{
    Task<PushSendOutcome> SendAsync(
        PushSubscription to,
        PushPayload payload,
        TimeSpan ttl,
        string? topic,
        CancellationToken ct
    );
}

// RFC 8030 delivery with RFC 8291 aes128gcm encryption and RFC 8292 `vapid t=, k=` authentication:
// the one combination every push service accepts, Apple's included (it refuses the older
// aesgcm/Crypto-Key form outright). One client for the process; the VAPID JWT is re-signed per call.
public sealed class WebPushSender : IPushSender, IDisposable
{
    readonly PushServiceClient? _client;
    readonly ILogger<WebPushSender> _log;

    public WebPushSender(PushOptions options, ILogger<WebPushSender> log)
    {
        _log = log;
        if (!options.Enabled)
            return;
        _client = new PushServiceClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            DefaultAuthentication = options.CreateAuthentication(),
            DefaultAuthenticationScheme = VapidAuthenticationScheme.Vapid,
            // A 429 is not worth holding the dispatcher for: the alert is only useful for minutes.
            AutoRetryAfter = false,
        };
    }

    public async Task<PushSendOutcome> SendAsync(
        PushSubscription to,
        PushPayload payload,
        TimeSpan ttl,
        string? topic,
        CancellationToken ct
    )
    {
        var client = _client ?? throw new InvalidOperationException("push notifications are not configured");
        var subscription = new WebPushSubscription { Endpoint = to.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, to.P256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, to.Auth);
        var message = new PushMessage(payload.ToJson())
        {
            TimeToLive = (int)ttl.TotalSeconds,
            Topic = topic,
            Urgency = PushMessageUrgency.High,
        };
        try
        {
            await client.RequestPushMessageDeliveryAsync(subscription, message, ct);
            return PushSendOutcome.Delivered;
        }
        catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushSendOutcome.Gone;
        }
        catch (PushServiceClientException e)
        {
            Log.PushRejected(_log, new Uri(to.Endpoint).Host, (int)e.StatusCode, e.Body ?? e.Message);
            return PushSendOutcome.Failed;
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            Log.PushRejected(_log, new Uri(to.Endpoint).Host, 0, e.Message);
            return PushSendOutcome.Failed;
        }
    }

    public void Dispose() => _client?.DefaultAuthentication?.Dispose();
}

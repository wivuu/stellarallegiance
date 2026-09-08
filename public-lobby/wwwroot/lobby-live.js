// Live server strip for the public pages ("/" and "/ladder").
//
// The lobby announces registry changes on GET /servers/live (anonymous SSE — PublicView.cs, route
// in PublicLobby.cs). This is the whole client half: re-raise each announcement as a `lobby-servers`
// DOM event on <body>, and let htmx do the rest — Pages/Shared/_ServerStrip.cshtml triggers its own
// `hx-get` off that event, so the section's markup is rendered in exactly one place (Razor) and
// there is no second copy of it here to drift.
//
// The stream carries the new state as JSON, but this side ignores the payload: htmx re-fetches the
// section, which is always the truth. Progressive enhancement — with no JavaScript (or if the
// stream never connects) the page keeps the strip ASP.NET rendered on the way out.
(function () {
    "use strict";

    if (typeof EventSource === "undefined" || typeof htmx === "undefined") return;

    // EventSource retries a dropped connection by itself, but gives up for good on a non-200 — the
    // stream sheds with 503 past its concurrency cap (PublicStreams). Reconnect those by hand,
    // backing off, and stop after a few tries rather than hammering a lobby that is already busy.
    var RETRY_DELAYS = [30000, 60000, 120000];
    var attempt = 0;

    function connect() {
        var source = new EventSource("/servers/live");

        source.addEventListener("snapshot", function () {
            attempt = 0;
            htmx.trigger(document.body, "lobby-servers");
        });

        source.addEventListener("error", function () {
            if (source.readyState !== EventSource.CLOSED) return; // transient; EventSource retries
            source.close();
            if (attempt < RETRY_DELAYS.length) setTimeout(connect, RETRY_DELAYS[attempt++]);
        });
    }

    connect();
})();

// Service worker for the lobby's Web Push notifications (issue #98). Root scope, registered by
// push.js when a pilot turns notifications on. It only shows what the lobby pushes (the payload is
// built by public-lobby/Notifications - PushPayload) and handles what a person can do with one:
// open the lobby, or "Mute these". No fetch handler: nothing is cached, the site works exactly as
// it does without it.
"use strict";

self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));

self.addEventListener("push", (event) => {
    let data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch {
        /* not ours / not JSON: still show something, a push must end in a notification */
    }
    const options = {
        body: data.body || "",
        icon: "/icon-192.png",
        data: { url: data.url || "/", event: data.event || null },
        // Same tag = the new one replaces the old (a re-sent ranked alert for one match never stacks).
        tag: data.tag || undefined,
        actions: data.event
            ? [
                  { action: "view", title: "View servers" },
                  { action: "mute", title: "Mute these" },
              ]
            : [],
    };
    event.waitUntil(self.registration.showNotification(data.title || "Stellar Allegiance", options));
});

self.addEventListener("notificationclick", (event) => {
    const notification = event.notification;
    const data = notification.data || {};
    notification.close();
    if (event.action === "mute" && data.event) {
        event.waitUntil(mute(data.event));
        return;
    }
    event.waitUntil(openLobby(data.url || "/"));
});

// "Mute these": turns the event off for the whole account (undo on /me). The subscription endpoint
// names the account - the worker has no session of its own.
async function mute(eventKey) {
    const subscription = await self.registration.pushManager.getSubscription();
    if (!subscription) return;
    await fetch("/push/mute", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ endpoint: subscription.endpoint, event: eventKey }),
    });
}

// Focus a lobby tab that is already open (moving it to the target page if it is elsewhere), else
// open one.
async function openLobby(url) {
    const target = new URL(url, self.location.origin);
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    const open = windows.find((w) => new URL(w.url).origin === self.location.origin);
    if (!open) {
        await self.clients.openWindow(target.href);
        return;
    }
    await open.focus();
    const at = new URL(open.url);
    if (at.pathname + at.hash !== target.pathname + target.hash && "navigate" in open) {
        try {
            await open.navigate(target.href);
        } catch {
            /* an uncontrolled tab can't be navigated from here; focusing it is enough */
        }
    }
}

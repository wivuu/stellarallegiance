// Web Push, browser half (issue #98). Drives two server-rendered surfaces and renders nothing itself:
//   - /me   Pages/Shared/_Notifications.cshtml  [data-push-surface="settings"]
//   - home  Pages/Shared/_PushPrompt.cshtml     [data-push-surface="prompt"]
// Every string and every URL it posts to lives in that markup. This file only does what a server
// can't - look at this browser (support, permission, its PushManager subscription), ask for
// permission, subscribe or unsubscribe - then writes what it found into the surface's
// [data-push-state] form and fires the custom event the matching htmx element listens for.
// The service worker it registers is /sw.js.
(function () {
    "use strict";

    const supported =
        window.isSecureContext &&
        "serviceWorker" in navigator &&
        "PushManager" in window &&
        "Notification" in window;

    const SNOOZE_KEY = "lobby.pushPrompt.snoozedUntil";
    const SNOOZE_MS = 30 * 24 * 60 * 60 * 1000;

    // The registration, if this browser ever turned notifications on here (never registers one).
    async function existingSubscription() {
        const registration = await navigator.serviceWorker.getRegistration("/");
        return registration ? registration.pushManager.getSubscription() : null;
    }

    function base64UrlToBytes(value) {
        const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
        return Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
    }

    function standalone() {
        return window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
    }

    // Permission, then the subscription. Null when the person didn't allow it.
    async function subscribe(vapidKey, fresh) {
        const permission = await Notification.requestPermission();
        if (permission !== "granted") return { permission, subscription: null };
        await navigator.serviceWorker.register("/sw.js", { scope: "/" });
        const registration = await navigator.serviceWorker.ready;
        let subscription = await registration.pushManager.getSubscription();
        // "Turn on again": the lobby pruned this one (the push service said it was gone), so get a new one.
        if (subscription && fresh) {
            await subscription.unsubscribe();
            subscription = null;
        }
        const options = { userVisibleOnly: true, applicationServerKey: base64UrlToBytes(vapidKey) };
        try {
            subscription = subscription || (await registration.pushManager.subscribe(options));
        } catch (err) {
            // Subscribed under an older VAPID key: drop that subscription and take a new one.
            if (err && err.name === "InvalidStateError") {
                const stale = await registration.pushManager.getSubscription();
                if (stale) await stale.unsubscribe();
                subscription = await registration.pushManager.subscribe(options);
            } else {
                throw err;
            }
        }
        return { permission, subscription };
    }

    function setField(form, name, value) {
        const input = form && form.elements.namedItem(name);
        if (input) input.value = value == null ? "" : String(value);
    }

    function toggle(surface, selector, show) {
        surface.querySelectorAll(selector).forEach((el) => (el.hidden = !show));
    }

    // ---- /me ----------------------------------------------------------------------------------

    async function probe(surface) {
        const form = surface.querySelector("[data-push-state]");
        setField(form, "PushSupported", supported);
        setField(form, "PushPermission", supported ? Notification.permission : "");
        let subscription = null;
        if (supported) {
            try {
                subscription = await existingSubscription();
            } catch {
                /* treat as none */
            }
        }
        setField(form, "PushEndpoint", subscription ? subscription.endpoint : "");
        htmx.trigger(surface, "push-probe");
    }

    async function turnOn(surface, button) {
        const form = surface.querySelector("[data-push-state]");
        toggle(surface, "[data-push-idle]", false);
        toggle(surface, "[data-push-failed]", false);
        toggle(surface, "[data-push-waiting]", true);
        try {
            const { permission, subscription } = await subscribe(
                surface.dataset.vapidKey,
                button.hasAttribute("data-push-resubscribe")
            );
            setField(form, "PushPermission", permission);
            if (!subscription) {
                // Refused or dismissed: /me re-renders (a refusal shows the "blocked" steps); the home
                // prompt just goes away when the answer was "denied".
                if (surface.dataset.pushSurface === "prompt") {
                    if (permission === "denied") surface.hidden = true;
                    toggle(surface, "[data-push-waiting]", false);
                    toggle(surface, "[data-push-idle]", true);
                } else {
                    setField(form, "PushEndpoint", "");
                    htmx.trigger(surface, "push-probe");
                }
                return;
            }
            const json = subscription.toJSON();
            setField(form, "PushEndpoint", json.endpoint);
            setField(form, "PushP256dh", json.keys && json.keys.p256dh);
            setField(form, "PushAuth", json.keys && json.keys.auth);
            setField(form, "PushStandalone", standalone());
            htmx.trigger(button, "push-subscribed");
        } catch (err) {
            console.warn("push: turning notifications on failed", err);
            toggle(surface, "[data-push-waiting]", false);
            toggle(surface, "[data-push-idle]", true);
            toggle(surface, "[data-push-failed]", true);
        }
    }

    async function turnOff(surface, button) {
        try {
            const subscription = await existingSubscription();
            if (subscription) await subscription.unsubscribe();
        } catch (err) {
            console.warn("push: unsubscribe failed", err);
        }
        setField(surface.querySelector("[data-push-state]"), "PushEndpoint", "");
        htmx.trigger(button, "push-unsubscribed");
    }

    // ---- home prompt --------------------------------------------------------------------------

    function snoozed() {
        try {
            return Number(localStorage.getItem(SNOOZE_KEY) || 0) > Date.now();
        } catch {
            return false;
        }
    }

    function snooze(surface) {
        try {
            localStorage.setItem(SNOOZE_KEY, String(Date.now() + SNOOZE_MS));
        } catch {
            /* private mode: hidden for this page view only */
        }
        surface.hidden = true;
    }

    // Shown only while this browser could turn notifications on and hasn't: supported, not blocked,
    // no subscription yet, and not snoozed with "Not now".
    async function maybeShowPrompt(surface) {
        if (!supported || snoozed() || Notification.permission === "denied") return;
        try {
            if (await existingSubscription()) return;
        } catch {
            /* show it */
        }
        surface.hidden = false;
    }

    // ---- wiring (delegated, so it survives htmx swaps) ------------------------------------------

    document.addEventListener("click", function (e) {
        const button = e.target.closest("[data-push-action]");
        const surface = button && button.closest("[data-push-surface]");
        if (!surface) return;
        switch (button.dataset.pushAction) {
            case "subscribe":
                if (supported) turnOn(surface, button);
                break;
            case "turn-off":
                turnOff(surface, button);
                break;
            case "probe":
                probe(surface);
                break;
            case "snooze":
                snooze(surface);
                break;
        }
    });

    document.addEventListener("DOMContentLoaded", function () {
        document.querySelectorAll('[data-push-surface="settings"]').forEach(probe);
        // Only a prompt still waiting to be offered starts hidden; its done state renders visible.
        document.querySelectorAll('[data-push-surface="prompt"][hidden]').forEach(maybeShowPrompt);
    });
})();

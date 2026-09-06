// Passkey ceremonies for /login (sign-in + sign-up) and /me (add a passkey) — plan
// .PLAN/LobbyRankingService.md §1.1, §3.1, WP0.3. Talks to the four JSON endpoints mapped in
// public-lobby/Hosting/WebAuth.cs (MapPasskeyEndpoints); the server never sees a raw credential
// object, only its .toJSON() form, so this is the ONLY place that shape is produced.
(function () {
    "use strict";

    function webAuthnAvailable() {
        return typeof window.PublicKeyCredential === "function";
    }

    function setStatus(el, message, isError) {
        if (!el) return;
        el.textContent = message || "";
        el.classList.toggle("text-danger", !!isError);
        el.classList.toggle("text-text-2", !isError);
    }

    async function postJson(url, body) {
        const res = await fetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            credentials: "include",
            body: JSON.stringify(body || {}),
        });
        let data = null;
        try {
            data = await res.json();
        } catch {
            /* empty/non-JSON body is fine for the *-options endpoints, which return raw WebAuthn JSON */
        }
        return { ok: res.ok, status: res.status, data };
    }

    function currentReturnUrl() {
        const input = document.getElementById("login-return-url");
        return input && input.value ? input.value : null;
    }

    function goTo(path) {
        window.location.assign(path || "/me");
    }

    // ---- Sign-in (discoverable credential; no display name needed) --------------------------------

    async function signIn(statusEl) {
        const optionsRes = await fetch("/login/passkey/request-options", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            credentials: "include",
            body: "{}",
        });
        if (!optionsRes.ok) {
            setStatus(statusEl, "Could not start passkey sign-in.", true);
            return;
        }
        const optionsJson = await optionsRes.json();
        const options = PublicKeyCredential.parseRequestOptionsFromJSON(optionsJson);

        let credential;
        try {
            credential = await navigator.credentials.get({ publicKey: options });
        } catch (err) {
            if (err && err.name === "NotAllowedError") return; // user cancelled
            setStatus(statusEl, "Passkey sign-in was cancelled or failed.", true);
            return;
        }

        const { ok, data } = await postJson("/login/passkey/assert", {
            credential: JSON.stringify(credential.toJSON()),
            returnUrl: currentReturnUrl(),
        });
        if (!ok) {
            setStatus(statusEl, (data && data.error) || "Sign-in failed.", true);
            return;
        }
        goTo(data && data.redirect);
    }

    // ---- Sign-up (fresh account, display name chosen by the user) ---------------------------------

    async function signUp(displayName, statusEl) {
        const optionsRes = await postJson("/login/passkey/creation-options", { displayName });
        if (!optionsRes.ok) {
            setStatus(statusEl, (optionsRes.data && optionsRes.data.error) || "Could not start passkey sign-up.", true);
            return;
        }
        const options = PublicKeyCredential.parseCreationOptionsFromJSON(optionsRes.data);

        let credential;
        try {
            credential = await navigator.credentials.create({ publicKey: options });
        } catch (err) {
            if (err && err.name === "NotAllowedError") return; // user cancelled
            setStatus(statusEl, "Passkey creation was cancelled or failed.", true);
            return;
        }

        const { ok, data } = await postJson("/login/passkey/register", {
            displayName,
            credential: JSON.stringify(credential.toJSON()),
            returnUrl: currentReturnUrl(),
        });
        if (!ok) {
            setStatus(statusEl, (data && data.error) || "Could not create your passkey account.", true);
            return;
        }
        goTo(data && data.redirect);
    }

    // ---- Add a passkey to the already-signed-in account (/me) --------------------------------------

    async function addPasskey(statusEl) {
        const optionsRes = await postJson("/login/passkey/creation-options", {});
        if (!optionsRes.ok) {
            setStatus(statusEl, (optionsRes.data && optionsRes.data.error) || "Could not start passkey setup.", true);
            return;
        }
        const options = PublicKeyCredential.parseCreationOptionsFromJSON(optionsRes.data);

        let credential;
        try {
            credential = await navigator.credentials.create({ publicKey: options });
        } catch (err) {
            if (err && err.name === "NotAllowedError") return;
            setStatus(statusEl, "Passkey creation was cancelled or failed.", true);
            return;
        }

        const { ok, data } = await postJson("/login/passkey/register", {
            credential: JSON.stringify(credential.toJSON()),
        });
        if (!ok) {
            setStatus(statusEl, (data && data.error) || "Could not save the passkey.", true);
            return;
        }
        setStatus(statusEl, "Passkey added. Reloading…", false);
        window.location.reload();
    }

    // ---- Wire up whichever elements are present on this page --------------------------------------

    document.addEventListener("DOMContentLoaded", function () {
        const signInBtn = document.getElementById("passkey-signin");
        const signUpForm = document.getElementById("passkey-signup");
        const addBtn = document.getElementById("add-passkey");
        const unavailable = document.getElementById("passkey-unavailable");
        const loginStatus = document.getElementById("passkey-status");
        const addStatus = document.getElementById("add-passkey-status");

        if (!webAuthnAvailable()) {
            if (unavailable) unavailable.hidden = false;
            if (signInBtn) signInBtn.disabled = true;
            if (signUpForm) {
                const submit = signUpForm.querySelector('button[type="submit"]');
                if (submit) submit.disabled = true;
            }
            if (addBtn) addBtn.disabled = true;
            return;
        }

        if (signInBtn) {
            signInBtn.addEventListener("click", function () {
                setStatus(loginStatus, "Waiting for your passkey…", false);
                signIn(loginStatus);
            });
        }

        if (signUpForm) {
            signUpForm.addEventListener("submit", function (e) {
                e.preventDefault();
                const displayName = new FormData(signUpForm).get("displayName");
                setStatus(loginStatus, "Creating your passkey…", false);
                signUp(displayName, loginStatus);
            });
        }

        if (addBtn) {
            addBtn.addEventListener("click", function () {
                setStatus(addStatus, "Waiting for your passkey…", false);
                addPasskey(addStatus);
            });
        }
    });
})();

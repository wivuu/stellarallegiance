---
status: accepted
date: 2026-09-06
---
# The public lobby is its own identity issuer

Players need an identity that survives sessions, proven to game servers the lobby does not run. We
decided the public lobby itself is the identity provider, built on ASP.NET Core Identity (.NET 10)
with env-gated external logins (Google, GitHub, Steam OpenID 2.0) and built-in passkeys, issuing its
own opaque session tokens and ES256-signed join tokens. We rejected Keycloak and Auth0 because Steam
only speaks OpenID 2.0, which neither supports without a bridge service, and because a managed IdP
adds a second service (and a JVM, for Keycloak) to a lobby that is one small Railway container.

## Consequences
- Passkeys make the lobby usable with zero provider configuration; every provider is optional.
- Game servers verify join tokens offline against the lobby's JWKS; the lobby records every issuance,
  which is the plausibility check on reported match results.
- Steam session tickets (in-client, needs an AppID) can be added later as a second login method for
  the same SteamID without changing this decision.

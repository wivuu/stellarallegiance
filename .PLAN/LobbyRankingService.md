**TODO: Research how the Steamworks API will impact this plan**

## High level plan
Update `lobby` service to allow retention of players across sessions. Players will be associated with an external identity, which can be used to authenticate them across sessions.

### Authentication & Identity
Clients, when they connect to a lobby, will be challenged to authenticate user their external identity. Ideally the client should launch a browser to login, and the client will either automatically receive an authentication token once the user signs in via browser.

Supported authentication providers:
- Google
- Apple
- Steam
- Github

It may make sense to host a keyclock or another managed identity service such as Auth0 to handle the authentication flow and token issuance.

The client can derive a unique token that can be passed to each game server which the game server can use to authenticate the player, but ensuring that the game server cannot reuse that token for malicious purposes or across sessions or other servers to impersonate (single use per-server).

Game servers must authenticate with the lobby through a similar mechanism, prompting the user to enter a code at a specific URL in order to authenticate their server with their external identity.

### Player Retention
Once authenticated, the lobby service will associate the player's session with their external identity. This allows the service to retain player information across sessions, enabling features such as persistent ranking, loadouts, and other player-specific data.

### Persistence
- Use a Postgresql (EF Core 10+) database for persistence
- Potentially use Garnet as a persistent caching layer
- Use Microsoft Orleans (10+) actors as service layer in front of most postgres calls to act as distributed in-memory cache.

Actors:
- PlayerGrain: Represents a player in the system. A player can be connected to 1 game server.
    - Tracks the player's connection to a game server and other player-specific data that needs to be retained across sessions.
    - Retains score from games across sessions, allowing the player's performance to be tracked over time.
- GameServerGrain: Represents a game server in the system.
    - Tracks the state and availability of the game server, including which players are connected to it and other server-specific data that needs to be retained across sessions.
    - These are things Lobby currently keeps in-memory
    - May have an associated GameGrain to track an active game hosted on this server.
- GameGrain: Represents a game in the system; tracks the state and progress of a game, including participating players, scores, and other relevant game-specific data.
    - Owned by GameServerGrain(s)
    - Maintains the state of the game, including which players are participating, their scores, and other game-specific information that needs to be retained across sessions.
    - Which map, game state, and other relevant game-specific information that needs to be retained in a distributed way

### Challenges
- Authenticating that what the game server reports about a player's actions or score is accurate and corresponds to the actual gameplay.
- Ensuring that the distributed in-memory cache (Orleans actors) remains consistent with the underlying Postgresql database.
- Handling scenarios where a player's session may be disconnected and later reconnected, ensuring that the player's state is correctly restored.
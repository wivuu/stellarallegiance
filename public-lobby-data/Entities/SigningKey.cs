namespace PublicLobby.Data.Entities;

// An ES256 keypair used to sign join tokens (plan §1.1/§3.2). Generated on first boot, published
// (public half only) at /.well-known/jwks.json keyed by Kid. Retiring a key (RetiredAt set) keeps
// it available for verifying already-issued-but-unexpired join tokens without letting it sign new
// ones — join tokens are 60s-lived, so retirement can drop a key from rotation almost immediately.
public class SigningKey
{
    // JWS "kid" header value.
    public required string Kid { get; init; }

    // PEM-encoded EC private key. Postgres-only secret (never leaves this table) — see plan §5
    // "signing keys (DB only)".
    public required string PrivatePem { get; set; }

    // Public key as a JWK JSON object, served verbatim at /.well-known/jwks.json.
    public required string PublicJwk { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

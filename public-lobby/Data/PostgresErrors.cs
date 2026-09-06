using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PublicLobby;

static class PostgresErrors
{
    // SQLSTATE 23505 = unique_violation; the citext display-name index and the device user-code
    // index both surface as this.
    public static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MoneroMarketCap.Data.Helpers;

/// <summary>Classifies database exceptions the app knows how to recover from.</summary>
public static class DbErrors
{
    /// <summary>PostgreSQL SQLSTATE for unique_violation.</summary>
    public const string UniqueViolation = "23505";

    /// <summary>True when the save failed because a row already exists for a unique index.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}

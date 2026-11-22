using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace PublicLobby.Data;

// Snake-cases every database identifier EF Core would otherwise name itself: tables, columns,
// primary/alternate keys, foreign keys, indexes and JSON container columns. LobbyDbContext adds it
// in OnConfiguring through SnakeCaseNamingOptionsExtension (below), so every host of the context
// (the lobby, the design-time factory, the tests) gets the same names without having to opt in.
//
// It replaces the EFCore.NamingConventions package (UseSnakeCaseNamingConvention), whose releases
// trail each EF Core major and cap EF Core below it, holding the whole EF stack back at every
// .NET release. The committed migrations and model snapshot were generated with that package, so
// the names produced here must stay byte-identical to its output for this model: SchemaTests checks
// HasPendingModelChanges against the snapshot, and `dotnet ef migrations
// has-pending-model-changes --project public-lobby-data` is the same check from the CLI.
//
// One pass at model finalization instead of the package's dozen incremental conventions: by then
// every entity, key, FK and index exists, so each name is rewritten once. Names are set at
// Convention level, so anything configured explicitly (ToTable, HasColumnName, HasName,
// HasDatabaseName, HasConstraintName) wins and is left alone, such as Identity's "UserNameIndex",
// "EmailIndex" and "RoleNameIndex", or device_codes.device_code. The one exception is the JSON
// container column of asp_net_user_passkeys: Identity names it after the property EXPLICITLY
// ("Data"), the package rewrote it anyway ("data"), and the table has that column, so it is
// rewritten unconditionally here too. Identity 10 mapped it as OwnsOne(p => p.Data).ToJson(),
// Identity 11 as ComplexProperty(p => p.Data).ToJson(): same jsonb column either way. Property
// names INSIDE the JSON document are not columns and stay as they are, as with the package.
//
// Only the mapping shapes this schema uses are handled. Anything else (inheritance, table
// splitting or sharing, owned types, complex properties not mapped to JSON, views, functions, SQL
// queries) throws when the model is built, so a new shape gets its naming rules added on purpose
// instead of quietly drifting from what the package would have produced.
public sealed class SnakeCaseNamingConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context
    )
    {
        var entityTypes = modelBuilder.Metadata.GetEntityTypes().ToList();
        ThrowIfUnsupported(entityTypes);

        // Tables, then columns, then constraints: EF derives the default key, FK and index names
        // from the CURRENT table and column names, so those must be snake_case before they're read.
        var tableTypes = entityTypes.Where(e => e.GetTableName() is not null).ToList();
        foreach (var entityType in tableTypes)
            entityType.Builder.ToTable(ToSnakeCase(entityType.GetTableName()!), entityType.GetSchema());

        foreach (var entityType in tableTypes)
        {
            var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                if (property.GetDefaultColumnName(table) is { } columnName)
                    property.Builder.HasColumnName(ToSnakeCase(columnName));
            }

            foreach (var complexProperty in entityType.GetComplexProperties())
            {
                if (complexProperty.ComplexType.GetContainerColumnName() is { } containerColumnName)
                    complexProperty.ComplexType.SetContainerColumnName(ToSnakeCase(containerColumnName));
            }
        }

        foreach (var entityType in tableTypes)
        {
            foreach (var key in entityType.GetDeclaredKeys())
            {
                if (key.GetDefaultName() is { } keyName)
                    key.Builder.HasName(ToSnakeCase(keyName));
            }

            foreach (var index in entityType.GetDeclaredIndexes())
            {
                if (index.GetDefaultDatabaseName() is { } indexName)
                    index.Builder.HasDatabaseName(ToSnakeCase(indexName));
            }
        }

        foreach (var entityType in entityTypes)
        {
            foreach (var foreignKey in entityType.GetDeclaredForeignKeys())
            {
                if (foreignKey.GetDefaultName() is { } foreignKeyName)
                    foreignKey.Builder.HasConstraintName(ToSnakeCase(foreignKeyName));
            }
        }
    }

    static void ThrowIfUnsupported(List<IConventionEntityType> entityTypes)
    {
        foreach (var entityType in entityTypes)
        {
            var unsupported =
                entityType.BaseType is not null || entityType.GetDirectlyDerivedTypes().Any() ? "an inheritance hierarchy"
                : entityType.FindOwnership() is not null ? "an owned type"
                : entityType.GetViewName() is not null ? "a view mapping"
                : entityType.GetSqlQuery() is not null ? "a SQL query mapping"
                : entityType.GetFunctionName() is not null ? "a function mapping"
                : entityType.GetComplexProperties().Any(p => !p.ComplexType.IsMappedToJson())
                    ? "a complex property not mapped to JSON"
                : null;
            if (unsupported is not null)
                throw Unsupported(entityType, unsupported);
        }

        var sharedTable = entityTypes
            .Where(e => e.GetTableName() is not null)
            .GroupBy(e => (e.GetSchema(), e.GetTableName()))
            .FirstOrDefault(g => g.Count() > 1);
        if (sharedTable is not null)
            throw Unsupported(sharedTable.First(), $"a table shared with other entity types ({sharedTable.Key.Item2})");

        static NotSupportedException Unsupported(IConventionEntityType entityType, string shape) =>
            new(
                $"{nameof(SnakeCaseNamingConvention)} does not handle {shape} (entity type '{entityType.DisplayName()}'). "
                    + "Add naming rules for it there, matching EFCore.NamingConventions' output, before mapping it."
            );
    }

    // Ported unchanged from EFCore.NamingConventions 10.0.1 (SnakeCaseNameRewriter, Apache License
    // 2.0, https://github.com/efcore/EFCore.NamingConventions) so every name matches the existing
    // schema, quirks included: "OAuthToken" becomes "o_auth_token" and "Address1Line" becomes
    // "address1line". The project sets InvariantGlobalization; the culture is pinned regardless.
    public static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + Math.Min(2, name.Length / 5));
        var previousCategory = default(UnicodeCategory?);

        for (var currentIndex = 0; currentIndex < name.Length; currentIndex++)
        {
            var currentChar = name[currentIndex];
            if (currentChar == '_')
            {
                builder.Append('_');
                previousCategory = null;
                continue;
            }

            var currentCategory = char.GetUnicodeCategory(currentChar);
            switch (currentCategory)
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                    if (
                        previousCategory == UnicodeCategory.SpaceSeparator
                        || previousCategory == UnicodeCategory.LowercaseLetter
                        || previousCategory != UnicodeCategory.DecimalDigitNumber
                            && previousCategory != null
                            && currentIndex > 0
                            && currentIndex + 1 < name.Length
                            && char.IsLower(name[currentIndex + 1])
                    )
                    {
                        builder.Append('_');
                    }

                    currentChar = char.ToLower(currentChar, CultureInfo.InvariantCulture);
                    break;

                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.DecimalDigitNumber:
                    if (previousCategory == UnicodeCategory.SpaceSeparator)
                        builder.Append('_');
                    break;

                default:
                    if (previousCategory != null)
                        previousCategory = UnicodeCategory.SpaceSeparator;
                    continue;
            }

            builder.Append(currentChar);
            previousCategory = currentCategory;
        }

        return builder.ToString();
    }
}

// Adds SnakeCaseNamingConvention to EF's provider convention set, the way the package did, rather
// than to one context's ConfigureConventions: EF builds a SECOND model from that convention set,
// for the __EFMigrationsHistory table (HistoryRepository), and the package snake-cased that table
// too. Every existing database (production included) has migration_id, product_version and
// pk___ef_migrations_history there; a history model built without the convention queries
// "MigrationId" and the migrate step fails before the lobby starts. SchemaTests pins the table's
// create script to the package's output.
public sealed class SnakeCaseNamingOptionsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => field ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        new EntityFrameworkServicesBuilder(services).TryAdd<IConventionSetPlugin, ConventionSetPlugin>();

    public void Validate(IDbContextOptions options) { }

    sealed class ConventionSetPlugin : IConventionSetPlugin
    {
        public ConventionSet ModifyConventions(ConventionSet conventionSet)
        {
            conventionSet.ModelFinalizingConventions.Add(new SnakeCaseNamingConvention());
            return conventionSet;
        }
    }

    sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "using SnakeCaseNamingConvention ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["SnakeCaseNaming"] = "1";
    }
}

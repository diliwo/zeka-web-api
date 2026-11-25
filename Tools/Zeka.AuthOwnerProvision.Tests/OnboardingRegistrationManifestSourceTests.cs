using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthManager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Zeka.PersistenceSecurity;

namespace Zeka.AuthOwnerProvision.Tests;

// Source/model and controlled-catalog guard tests only. No database connection is
// configured or opened, and these results do not constitute PostgreSQL evidence.
[Trait("Issue", "ZEK-27")]
[Trait("Evidence", "SourceOnly")]
public sealed class OnboardingRegistrationManifestSourceTests
{
    private const string Migration = "20261124170000_OrganisationSingleActiveOwner";
    private const string PreviousManifestHash = "6543c54a1d7ab5539ef321af8e81c0a5a0692f3378912a5926fd159432e68ad6";
    private static readonly string[] EnrollmentTables =
    [
        "EmailVerificationChallenges", "OnboardingGrants", "OnboardingOperationReceipts",
        "VerificationMailOutboxMessages"
    ];
    private static readonly string[] EnrollmentUpdates =
    [
        "EmailVerificationChallenges|Status", "EmailVerificationChallenges|TransitionedAtUtc",
        "EmailVerificationChallenges|Version", "VerificationMailOutboxMessages|Attempts",
        "VerificationMailOutboxMessages|DeliveredAtUtc", "VerificationMailOutboxMessages|LeaseExpiresAtUtc",
        "VerificationMailOutboxMessages|LeaseId", "VerificationMailOutboxMessages|NextAttemptAtUtc",
        "VerificationMailOutboxMessages|ObsoleteAtAcceptance", "VerificationMailOutboxMessages|Outcome",
        "VerificationMailOutboxMessages|Status", "VerificationMailOutboxMessages|Version"
    ];

    [Fact]
    public void Embedded_successor_classifies_every_exact_EF_table_once_and_targets_the_existing_migration()
    {
        var manifest = Active();
        using var connection = new NpgsqlConnection();
        using var database = Model(connection);
        var mapped = database.Model.GetEntityTypes()
            .Select(type => new ManagedObjectIdentity(type.GetSchema() ?? "public", type.GetTableName()!))
            .Distinct().OrderBy(Key, StringComparer.Ordinal).ToArray();
        var classified = manifest.ProtectedTables.Concat(manifest.ExcludedTables).OrderBy(Key, StringComparer.Ordinal).ToArray();

        Assert.Equal(21, manifest.SchemaVersion);
        Assert.Equal(Migration, manifest.MigrationId);
        Assert.Equal(Migration, database.Database.GetMigrations().Last());
        Assert.Equal(45, mapped.Length);
        Assert.Equal(23, manifest.ProtectedTables.Length);
        Assert.Equal(22, manifest.ExcludedTables.Length);
        Assert.Equal(classified.Length, classified.Distinct().Count());
        Assert.Equal(mapped, classified);
        Assert.Equal(manifest.ProtectedTables.OrderBy(Key, StringComparer.Ordinal),
            RlsSecurityManifestVerifier.GenerateProtectedInventory(database).OrderBy(Key, StringComparer.Ordinal));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Validate("ValidateManagedObjectManifest", manifest);
        Validate("ValidateDefaultPrivilegeManifest", manifest);
        Validate("ValidateParameterPrivilegeManifest", manifest);
    }

    [Fact]
    public void Successor_preserves_all_previous_classifications_roles_policies_and_security_contracts()
    {
        var previous = Previous();
        var current = Active();
        Assert.Equal(previous.ProtectedTables, current.ProtectedTables);
        Assert.Equal(previous.ExcludedTables, current.ExcludedTables.Where(table => !Enrollment(table)).ToArray());
        Assert.Equal(Serialized(previous.TablePrivileges), Serialized(current.TablePrivileges.Where(row => !Enrollment(row.Object)).ToArray()));
        Assert.Equal(Serialized(previous.ColumnPrivileges), Serialized(current.ColumnPrivileges!.Where(row => !Enrollment(row.Object)).ToArray()));

        var oldUnchanged = JsonNode.Parse(Serialized(previous))!.AsObject();
        var newUnchanged = JsonNode.Parse(Serialized(current))!.AsObject();
        foreach (var changed in new[] { "SchemaVersion", "MigrationId", "ExcludedTables", "TablePrivileges", "ColumnPrivileges" })
        {
            oldUnchanged.Remove(changed);
            newUnchanged.Remove(changed);
        }
        Assert.True(JsonNode.DeepEquals(oldUnchanged, newUnchanged), "An unrelated manifest contract changed.");
        Assert.Equal("zeka_auth_owner", current.OwnerRole);
        Assert.Equal("zeka_auth_migrator", current.MigratorRole);
        Assert.Equal("zeka_auth_runtime", current.RuntimeRole);
        Assert.Equal("zeka_auth_closure_recovery", current.RecoveryRole);
    }

    [Fact]
    public void Historical_v19_security_contract_remains_accepted_by_the_unchanged_prior_guards()
    {
        var previous = Previous();
        Assert.Equal(19, previous.SchemaVersion);
        Validate("ValidateManagedObjectManifest", previous);
        Validate("ValidateDefaultPrivilegeManifest", previous);
        Validate("ValidateParameterPrivilegeManifest", previous);
    }

    [Fact]
    [Trait("Issue", "ZEK-50")]
    public void Active_owner_index_manifest_changes_only_the_approved_version_and_history_identity()
    {
        var bytes = File.ReadAllBytes(Path.Combine(SourceRoot(), "Services/AuthManager/AuthManager.Infrastructure/Security/rls-manifest.v20.json"));
        Assert.Equal("6eca66966a8957a7dfc558d308992c2a92e9dd324ae3bcf2b232f583f314b944",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var previous = JsonNode.Parse(bytes)!.AsObject();
        var current = JsonNode.Parse(File.ReadAllBytes(Path.Combine(SourceRoot(),
            "Services/AuthManager/AuthManager.Infrastructure/Security/rls-manifest.v21.json")))!.AsObject();
        Assert.Equal(20, previous["schemaVersion"]!.GetValue<int>());
        Assert.Equal("20261115195218_OnboardingRegistration", previous["migrationId"]!.GetValue<string>());
        Assert.Equal(21, current["schemaVersion"]!.GetValue<int>());
        Assert.Equal(Migration, current["migrationId"]!.GetValue<string>());
        foreach (var field in new[] { "schemaVersion", "migrationId" })
        { previous.Remove(field); current.Remove(field); }
        Assert.True(JsonNode.DeepEquals(previous, current), "A privilege, role, classification or policy changed.");
    }

    [Theory]
    [InlineData(22, "AuthManagement")]
    [InlineData(21, "AdminAreaManagement")]
    [InlineData(21, "ClientManagement")]
    [InlineData(20, "ClientManagement")]
    public async Task Unknown_schema_and_non_Auth_v20_remain_rejected_before_any_connection(int schema, string service)
    {
        var changed = Active() with { SchemaVersion = schema, Service = service };
        await RejectedBeforeConnection(changed, "Manifest identity does not match the deployment model.");
    }
    [Fact]
    public void Four_platform_identity_tables_have_exact_source_required_grants_without_tenant_classification()
    {
        var manifest = Active();
        var previous = Previous();
        var added = manifest.ExcludedTables.Except(previous.ExcludedTables).OrderBy(table => table.Name, StringComparer.Ordinal).ToArray();
        Assert.Equal(EnrollmentTables, added.Select(table => table.Name));
        Assert.All(added, table =>
        {
            Assert.Equal("public", table.Schema);
            Assert.DoesNotContain(table, manifest.ProtectedTables);
            Assert.DoesNotContain(table, manifest.GlobalControlPlaneTables!); // This field is the lifecycle-only subclass.
            var acl = Assert.Single(manifest.TablePrivileges.Where(row => row.Object == table));
            Assert.Equal(new[] { "INSERT", "SELECT" }, acl.Privileges);
        });
        var updates = manifest.ColumnPrivileges!.Where(row => Enrollment(row.Object)).ToArray();
        Assert.Equal(EnrollmentUpdates, updates.Select(row => row.Object.Name + "|" + row.Column));
        Assert.All(updates, row =>
        {
            Assert.False(row.Grantable);
            Assert.Equal(new[] { "UPDATE" }, row.Privileges);
        });
        Assert.DoesNotContain(updates, row => row.Object.Name is "OnboardingGrants" or "OnboardingOperationReceipts");
        using var connection = new NpgsqlConnection();
        using var database = Model(connection);
        Assert.All(database.Model.GetEntityTypes().Where(type => EnrollmentTables.Contains(type.GetTableName(), StringComparer.Ordinal)),
            type => Assert.Null(type.FindProperty("OrganisationId")));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void Platform_exclusions_have_specific_architectural_and_implementation_references()
    {
        var path = Path.Combine(SourceRoot(), "Services/AuthManager/AuthManager.Infrastructure/Security/OnboardingRegistrationSecurityContract.md");
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        var text = string.Join('\n', lines);
        Assert.Contains("ADR-003", text);
        Assert.Contains("PostgreSQL RLS and Runtime Role Separation", text);
        foreach (var table in EnrollmentTables)
        {
            var start = Assert.Single(Enumerable.Range(0, lines.Length).Where(index => lines[index].StartsWith('#')
                && lines[index].TrimStart('#', ' ').Trim('`') == table));
            var next = Enumerable.Range(start + 1, lines.Length - start - 1).FirstOrDefault(index => lines[index].StartsWith('#'), lines.Length);
            var section = string.Join('\n', lines[(start + 1)..next]);
            Assert.Contains(table == "VerificationMailOutboxMessages" ? "VerificationMailDispatcher.cs" : "RegistrationService.cs", section);
        }
    }

    [Fact]
    public async Task Missing_ordinary_classification_is_rejected_by_the_actual_complete_model_guard()
    {
        var manifest = Active();
        var missing = new ManagedObjectIdentity("public", "AspNetUserTokens");
        var changed = manifest with
        {
            ExcludedTables = manifest.ExcludedTables.Where(table => table != missing).ToArray(),
            TablePrivileges = manifest.TablePrivileges.Where(row => row.Object != missing).ToArray()
        };
        await RejectedBeforeConnection(changed, "EF complete classification drifted.");
    }

    [Fact]
    public async Task Unknown_classification_is_rejected_by_the_actual_complete_model_guard()
    {
        var manifest = Active();
        var unknown = new ManagedObjectIdentity("public", "UnreviewedFixtureTable");
        var changed = manifest with
        {
            ExcludedTables = manifest.ExcludedTables.Append(unknown).OrderBy(Key, StringComparer.Ordinal).ToArray(),
            TablePrivileges = manifest.TablePrivileges.Append(new ObjectPrivilegeState(unknown, ["INSERT", "SELECT"]))
                .OrderBy(row => Key(row.Object), StringComparer.Ordinal).ToArray()
        };
        await RejectedBeforeConnection(changed, "EF complete classification drifted.");
    }

    [Fact]
    public async Task Duplicate_classification_is_rejected_by_the_actual_manifest_guard()
    {
        var manifest = Active();
        var repeated = manifest.ExcludedTables.Single(table => table.Name == "AspNetUserTokens");
        var changed = manifest with { ExcludedTables = manifest.ExcludedTables.Append(repeated).OrderBy(Key, StringComparer.Ordinal).ToArray() };
        await RejectedBeforeConnection(changed, "The excluded table inventory is invalid.");
    }

    [Fact]
    public async Task An_unapproved_privilege_is_still_rejected_by_the_actual_manifest_guard()
    {
        var manifest = Active();
        var changed = manifest with
        {
            TablePrivileges = manifest.TablePrivileges.Select(row => row.Object.Name == "AspNetUserTokens"
                ? row with { Privileges = ["TRUNCATE"] } : row).ToArray()
        };
        await RejectedBeforeConnection(changed, "The table privilege inventory is invalid.");
    }

    [Theory]
    [InlineData("missing-old")]
    [InlineData("missing-new")]
    [InlineData("extra-grant-update")]
    [InlineData("extra-receipt-update")]
    [InlineData("grantable")]
    public async Task Exact_column_inventory_rejects_missing_old_or_new_and_unapproved_mutation(string tamper)
    {
        var manifest = Active();
        var columns = manifest.ColumnPrivileges!;
        var changed = manifest with
        {
            ColumnPrivileges = tamper switch
            {
                "missing-old" => columns.Where(row => row.Object.Name != "BrowserRefreshCredentials" || row.Column != "ConsumedAtUtc").ToArray(),
                "missing-new" => columns.Where(row => row.Object.Name != "EmailVerificationChallenges" || row.Column != "Version").ToArray(),
                "extra-grant-update" => columns.Append(new ColumnPrivilegeState(new("public", "OnboardingGrants"), "ConsumedAtUtc", ["UPDATE"], false))
                    .OrderBy(row => Key(row.Object) + "|" + row.Column, StringComparer.Ordinal).ToArray(),
                "extra-receipt-update" => columns.Append(new ColumnPrivilegeState(new("public", "OnboardingOperationReceipts"), "Kind", ["UPDATE"], false))
                    .OrderBy(row => Key(row.Object) + "|" + row.Column, StringComparer.Ordinal).ToArray(),
                "grantable" => columns.Select(row => row.Object.Name == "EmailVerificationChallenges" && row.Column == "Status"
                    ? row with { Grantable = true } : row).ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(tamper))
            }
        };
        await RejectedBeforeConnection(changed, "Lifecycle column privilege inventory is invalid.");
    }

    public static IEnumerable<object[]> InvalidEnrollmentTableGrants()
    {
        foreach (var table in EnrollmentTables)
        foreach (var tamper in new[] { "update", "delete", "missing-insert", "missing-select" })
            yield return [table, tamper];
    }

    [Theory]
    [MemberData(nameof(InvalidEnrollmentTableGrants))]
    public async Task Every_new_table_requires_exact_INSERT_SELECT_without_broader_mutation(string table, string tamper)
    {
        var manifest = Active();
        var privileges = tamper switch
        {
            "update" => new[] { "INSERT", "SELECT", "UPDATE" },
            "delete" => new[] { "DELETE", "INSERT", "SELECT" },
            "missing-insert" => new[] { "SELECT" },
            "missing-select" => new[] { "INSERT" },
            _ => throw new ArgumentOutOfRangeException(nameof(tamper))
        };
        var changed = manifest with
        {
            TablePrivileges = manifest.TablePrivileges.Select(row => row.Object.Name == table
                ? row with { Privileges = privileges } : row).ToArray()
        };
        await RejectedBeforeConnection(changed, "Onboarding table privileges are invalid.");
    }
    [Fact]
    public async Task Wrong_history_marker_is_still_rejected_after_valid_table_and_tenancy_catalogs()
    {
        var manifest = Active();
        using var catalog = new CatalogFixture(manifest, "20261018144506_BrowserSessionSnapshot");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyCatalog(catalog, manifest));
        Assert.Equal("Migration-history identity or ownership drifted.", error.Message);
        Assert.Equal(3, catalog.Commands);
    }

    [Fact]
    public async Task Matching_history_advances_to_the_existing_policy_guard_as_a_discriminating_control()
    {
        var manifest = Active();
        using var catalog = new CatalogFixture(manifest, Migration);
        await Assert.ThrowsAsync<PolicyBoundaryReached>(() => VerifyCatalog(catalog, manifest));
        Assert.Equal(4, catalog.Commands);
    }

    [Theory]
    [InlineData("protected-rls-disabled")]
    [InlineData("excluded-rls-enabled")]
    [InlineData("wrong-owner")]
    public async Task Existing_catalog_ownership_and_RLS_guards_remain_fail_closed(string tamper)
    {
        var manifest = Active();
        using var catalog = new CatalogFixture(manifest, Migration, tamper);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyCatalog(catalog, manifest));
        Assert.Equal("Table ownership or RLS state drifted.", error.Message);
        Assert.Equal(1, catalog.Commands);
    }

    private static RlsSecurityManifest Active() => RlsSecurityManifestVerifier.Load(typeof(AuthDbContext).Assembly);
    private static bool Enrollment(ManagedObjectIdentity identity) => identity.Schema == "public" && EnrollmentTables.Contains(identity.Name, StringComparer.Ordinal);
    private static string Key(ManagedObjectIdentity identity) => identity.Schema + "." + identity.Name;
    private static string Serialized<T>(T value) => JsonSerializer.Serialize(value);
    private static AuthDbContext Model(NpgsqlConnection connection) => new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);

    private static string SourceRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        const string relative = "Services/AuthManager/AuthManager.Infrastructure/Security/rls-manifest.v19.json";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative))) root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }

    private static RlsSecurityManifest Previous()
    {
        var bytes = File.ReadAllBytes(Path.Combine(SourceRoot(), "Services/AuthManager/AuthManager.Infrastructure/Security/rls-manifest.v19.json"));
        Assert.Equal(PreviousManifestHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return JsonSerializer.Deserialize<RlsSecurityManifest>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static async Task RejectedBeforeConnection(RlsSecurityManifest manifest, string expectedPrefix)
    {
        using var connection = new NpgsqlConnection(); // No endpoint or credential is configured.
        using var database = Model(connection);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RlsSecurityManifestVerifier.VerifyAsync(database, manifest));
        Assert.StartsWith(expectedPrefix, error.Message);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    private static void Validate(string name, RlsSecurityManifest manifest)
    {
        var method = typeof(RlsSecurityManifestVerifier).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try { method!.Invoke(null, [manifest]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static async Task VerifyCatalog(DbConnection connection, RlsSecurityManifest manifest)
    {
        var method = typeof(RlsSecurityManifestVerifier).GetMethod("VerifyTablesAndPolicies", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        await (Task)method!.Invoke(null, [connection, manifest, CancellationToken.None])!;
    }

    private sealed class PolicyBoundaryReached : Exception;

    // Three controlled catalog result shapes exercise the actual history/RLS guards.
    // A fourth command deliberately stops at the policy boundary. No provider or SQL
    // is executed; actual PostgreSQL verification remains a separate acceptance gate.
    private sealed class CatalogFixture(RlsSecurityManifest manifest, string history, string? tamper = null) : DbConnection
    {
        public int Commands { get; private set; }
        [AllowNull] public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "source-only-catalog";
        public override string DataSource => "none";
        public override string ServerVersion => "source-only";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new InvalidOperationException("A source-only fixture must never open a connection.");
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new CatalogCommand(this);

        public DbDataReader Read(string sql)
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE"));
            var stage = ++Commands;
            if (stage == 1)
            {
                Assert.Contains("c.relrowsecurity::text,c.relforcerowsecurity::text", sql);
                var firstProtected = manifest.ProtectedTables[0];
                return Rows(5, manifest.ProtectedTables.Concat(manifest.ExcludedTables).Append(manifest.MigrationHistoryTable)
                    .OrderBy(Key, StringComparer.Ordinal).Select(table =>
                    {
                        var tenant = manifest.ProtectedTables.Contains(table);
                        var wrong = tamper == "wrong-owner" && table == firstProtected;
                        if (tamper == "protected-rls-disabled" && table == firstProtected) tenant = false;
                        if (tamper == "excluded-rls-enabled" && table.Name == "EmailVerificationChallenges") tenant = true;
                        return new[] { table.Schema, table.Name, wrong ? "unreviewed_fixture_owner" : manifest.OwnerRole,
                            tenant ? "true" : "false", tenant ? "true" : "false" };
                    }));
            }
            if (stage == 2)
            {
                Assert.Contains("a.attname='OrganisationId'", sql);
                return Rows(4, manifest.ProtectedTables.Select(table => new[] { table.Schema, table.Name, "uuid", "true" }));
            }
            if (stage == 3)
            {
                Assert.Contains("h.\"MigrationId\"", sql);
                return Rows(4, [new[] { manifest.OwnerRole, "false", "false", history }]);
            }
            Assert.Equal(4, stage);
            Assert.Contains("pg_catalog.pg_policy", sql);
            throw new PolicyBoundaryReached();
        }

        private static DbDataReader Rows(int fields, IEnumerable<string[]> rows)
        {
            var table = new DataTable();
            for (var index = 0; index < fields; index++) table.Columns.Add("field" + index, typeof(string));
            foreach (var row in rows) table.Rows.Add(row.Cast<object>().ToArray());
            return table.CreateDataReader();
        }
    }

    private sealed class CatalogCommand(CatalogFixture fixture) : DbCommand
    {
        private readonly SqliteCommand parameterStore = new();
        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = fixture;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameterStore.Parameters;
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new SqliteParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => fixture.Read(CommandText);
        protected override void Dispose(bool disposing) { if (disposing) parameterStore.Dispose(); base.Dispose(disposing); }
    }
}

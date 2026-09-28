using AuthManager.Application.Authorization;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Lifecycle;
using AuthManager.Infrastructure.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;
using Zeka.PersistenceSecurity;
using Zeka.Lifecycle.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "LIFE-FND-01")]
public sealed class LifecycleFrozenRegistryEvidenceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private const string FixturePassword = "synthetic-lifecycle-fixture";
    private static readonly Guid Organisation = Guid.Parse("46000000-0000-0000-0000-000000000001");
    private static readonly Guid RevisionOne = Guid.Parse("46000000-0000-0000-0000-000000000101");
    private static readonly Guid RevisionTwo = Guid.Parse("46000000-0000-0000-0000-000000000102");

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        Assert.Equal(false, await Sql(postgres.GetConnectionString(),
            "SELECT pg_catalog.to_regnamespace('zeka') IS NOT NULL"));
        await Sql(postgres.GetConnectionString(), Bootstrap());
        await Sql(postgres.GetConnectionString(), $"""
            ALTER ROLE zeka_auth_migrator PASSWORD '{FixturePassword}';
            ALTER ROLE zeka_auth_runtime PASSWORD '{FixturePassword}';
            """);
        await using (var migration = Context("zeka_auth_migrator"))
        {
            await migration.Database.OpenConnectionAsync();
            await migration.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await migration.Database.MigrateAsync();
        }
        await Sql(postgres.GetConnectionString(), Bootstrap());
        await using var verification = Context("zeka_auth_migrator");
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
        output.WriteLine("Provisioning -> restricted migration -> administrative reconciliation -> v11 verification passed.");
        output.WriteLine("PostgreSQL: " + await Sql(postgres.GetConnectionString(), "SELECT version()"));
    }

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Ordinary_runtime_cannot_rebind_an_admitted_operation_to_a_successor_registry()
    {
        var first = Registry(RevisionOne, "participant-original");
        var successor = Registry(RevisionTwo, "participant-successor");
        Assert.True(await Activate(first, 0));
        var store = new LifecycleAdmissionStore(Options("zeka_auth_runtime"), TimeProvider.System,
            new FixtureExportInventory());
        var admission = new LifecycleAdmission(new CurrentAccess(), store);
        var result = await admission.AdmitAsync(Guid.Parse("46000000-0000-0000-0000-000000000010"),
            Organisation, LifecycleOperationFamily.Export, Guid.Parse("46000000-0000-0000-0000-000000000020"));
        Assert.Equal(AdmissionStatus.Admitted, result.Status);
        var operation = Assert.IsType<LifecycleOperation>(result.Operation);
        Assert.Equal(RevisionOne, operation.RegistryRevision);
        Assert.Equal(first.InventoryHash, operation.InventoryHash);
        Assert.Single(operation.Participants);
        Assert.True(await Activate(successor, 1));
        output.WriteLine($"Fixture revision: {RevisionOne:D}; inventory hash: {first.InventoryHash}");
        output.WriteLine($"Successor revision: {RevisionTwo:D}; inventory hash: {successor.InventoryHash}");

        await using var connection = new NpgsqlConnection(Connection("zeka_auth_runtime"));
        await connection.OpenAsync();
        Assert.Equal("zeka_auth_runtime", await new NpgsqlCommand("SELECT session_user", connection).ExecuteScalarAsync());
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("organisation", Organisation.ToString("D"));
            await context.ExecuteScalarAsync();
        }
        await using var mutation = new NpgsqlCommand("""
            UPDATE public."OrganisationLifecycleOperations"
            SET "RegistryRevision"=@successor, "InventoryHash"=@hash
            WHERE "Id"=@operation
            RETURNING "RegistryRevision"::text || '|' || "InventoryHash"
            """, connection, transaction);
        mutation.Parameters.AddWithValue("successor", RevisionTwo);
        mutation.Parameters.AddWithValue("hash", successor.InventoryHash);
        mutation.Parameters.AddWithValue("operation", operation.Id);
        var error = await Record.ExceptionAsync(async () =>
        {
            var changed = await mutation.ExecuteScalarAsync();
            output.WriteLine("Runtime frozen-identity mutation returned: " + changed);
        });
        // Roll back the adversarial probe regardless of the outcome; do not repair the schema.
        await transaction.RollbackAsync();
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(error).SqlState);
    }

    [Fact]
    public async Task Every_live_and_EF_frozen_operation_column_rejects_runtime_update_without_mutation()
    {
        var operation = await Seed();
        await using var model = Context("zeka_auth_runtime");
        var table = StoreObjectIdentifier.Table("OrganisationLifecycleOperations", null);
        var ef = model.Model.FindEntityType(typeof(LifecycleOperation))!.GetProperties()
            .Select(property => property.GetColumnName(table)!).Order(StringComparer.Ordinal).ToArray();
        var live = (string)(await Sql(postgres.GetConnectionString(), """
            SELECT string_agg(attname,',' ORDER BY attname COLLATE "C") FROM pg_catalog.pg_attribute
            WHERE attrelid='public."OrganisationLifecycleOperations"'::regclass AND attnum>0 AND NOT attisdropped
            """))!;
        Assert.Equal(ef, live.Split(','));
        output.WriteLine("Exact EF/live operation columns: " + live);
        var before = await Row(operation.Id);
        var workflowMutable = new[] { "State", "Revision", "SnapshotAt", "FenceEvidenceHash",
            "PackageSha256", "PackageReference", "FailureCode", "CompletedAt", "IsActive" };
        foreach (var column in ef.Except(workflowMutable))
        {
            var property = model.Model.FindEntityType(typeof(LifecycleOperation))!.GetProperties()
                .Single(p => p.GetColumnName(table) == column);
            var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(column);
            var change = property.ClrType == typeof(Guid) ? "'46000000-0000-0000-0000-000000009999'::uuid"
                : property.ClrType == typeof(string) ? quoted + " || '-changed'"
                : property.ClrType == typeof(bool) ? "NOT " + quoted
                : property.ClrType == typeof(DateTimeOffset) ? quoted + " + interval '1 second'"
                : quoted + " + 1";
            var exception = await Assert.ThrowsAsync<PostgresException>(() => Runtime(Organisation,
                $"UPDATE public.\"OrganisationLifecycleOperations\" SET {quoted}={change} WHERE \"Id\"='{operation.Id:D}'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
            Assert.Equal(before, await Row(operation.Id));
            output.WriteLine($"Frozen column {column}: SQLSTATE 42501; row unchanged.");
        }
    }

    [Fact]
    public async Task Allowed_column_ACL_and_cross_organisation_RLS_are_independent()
    {
        var operation = await Seed();
        // This is an ACL/concurrency probe, not authorization for a later workflow transition.
        var update = $"""
            UPDATE public."OrganisationLifecycleOperations"
            SET "State"="State", "Revision"="Revision"+1
            WHERE "Id"='{operation.Id:D}' AND "Revision"=1 RETURNING "Revision"
            """;
        Assert.Null(await Runtime(Guid.Parse("46000000-0000-0000-0000-000000000002"), update));
        Assert.Equal(1L, await Runtime(Organisation,
            $"SELECT \"Revision\" FROM public.\"OrganisationLifecycleOperations\" WHERE \"Id\"='{operation.Id:D}'"));
        Assert.Equal(2L, await Runtime(Organisation, update));
        Assert.Null(await Runtime(Organisation, update)); // stale expected revision cannot update
        Assert.Equal(2L, await Runtime(Organisation,
            $"SELECT \"Revision\" FROM public.\"OrganisationLifecycleOperations\" WHERE \"Id\"='{operation.Id:D}'"));
    }

    [Fact]
    public async Task Bootstrap_removes_stale_table_and_all_column_grants_and_preserves_future_column_default()
    {
        await Seed();
        await Sql(postgres.GetConnectionString(), """
            ALTER TABLE public."OrganisationLifecycleOperations" ADD COLUMN "FutureProbe" integer;
            GRANT UPDATE ON public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
            GRANT UPDATE ("RegistryRevision", "InventoryHash", "FutureProbe")
              ON public."OrganisationLifecycleOperations" TO zeka_auth_runtime WITH GRANT OPTION;
            GRANT SELECT ("Id") ON public."OrganisationLifecycleOperations" TO PUBLIC;
            GRANT UPDATE ("Id") ON public."OrganisationLifecycleOperations" TO zeka_auth_migrator;
            """);
        await Sql(postgres.GetConnectionString(), Bootstrap());
        await Sql(postgres.GetConnectionString(), Bootstrap());
        Assert.Equal(false, await Sql(postgres.GetConnectionString(), """
            SELECT has_table_privilege('zeka_auth_runtime','public."OrganisationLifecycleOperations"','UPDATE')
              OR has_column_privilege('zeka_auth_runtime','public."OrganisationLifecycleOperations"','FutureProbe','UPDATE')
              OR has_column_privilege('zeka_auth_runtime','public."OrganisationLifecycleOperations"','RegistryRevision','UPDATE')
              OR has_column_privilege('zeka_auth_migrator','public."OrganisationLifecycleOperations"','Id','UPDATE')
            """));
        await using var verification = Context("zeka_auth_migrator");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly));
        Assert.Contains("EF/live operation columns", exception.Message);
        await Sql(postgres.GetConnectionString(), "ALTER TABLE public.\"OrganisationLifecycleOperations\" DROP COLUMN \"FutureProbe\"");
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
    }

    [Fact]
    public async Task Manifest_rejects_each_frozen_grant_missing_grant_table_update_grantee_and_grant_option()
    {
        await using var model = Context("zeka_auth_migrator");
        var table = StoreObjectIdentifier.Table("OrganisationLifecycleOperations", null);
        var mutable = new[] { "State", "Revision", "SnapshotAt", "FenceEvidenceHash", "PackageSha256",
            "PackageReference", "FailureCode", "CompletedAt", "IsActive" };
        var frozen = model.Model.FindEntityType(typeof(LifecycleOperation))!.GetProperties()
            .Select(p => p.GetColumnName(table)!).Except(mutable).Order(StringComparer.Ordinal);
        var mutations = frozen.Select(column =>
            $"GRANT UPDATE ({new NpgsqlCommandBuilder().QuoteIdentifier(column)}) ON public.\"OrganisationLifecycleOperations\" TO zeka_auth_runtime")
            .Concat(new[]
            {
                "REVOKE UPDATE (\"State\") ON public.\"OrganisationLifecycleOperations\" FROM zeka_auth_runtime",
                "REVOKE UPDATE (\"Revision\") ON public.\"OrganisationLifecycleOperations\" FROM zeka_auth_runtime",
                "GRANT UPDATE ON public.\"OrganisationLifecycleOperations\" TO zeka_auth_runtime",
                "GRANT UPDATE (\"State\") ON public.\"OrganisationLifecycleOperations\" TO zeka_auth_migrator",
                "GRANT UPDATE (\"Revision\") ON public.\"OrganisationLifecycleOperations\" TO PUBLIC",
                "GRANT UPDATE (\"State\") ON public.\"OrganisationLifecycleOperations\" TO zeka_auth_runtime WITH GRANT OPTION"
            });
        foreach (var mutation in mutations)
        {
            await Sql(postgres.GetConnectionString(), mutation);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RlsSecurityManifestVerifier.VerifyAsync(model, typeof(AuthDbContext).Assembly));
            Assert.Contains("ACLs", exception.Message);
            output.WriteLine("Rejected catalog drift: " + mutation);
            await Sql(postgres.GetConnectionString(), Bootstrap());
            await RlsSecurityManifestVerifier.VerifyAsync(model, typeof(AuthDbContext).Assembly);
        }
    }

    [Fact]
    public async Task Runtime_registry_mutation_lifecycle_delete_and_participant_identity_update_are_denied()
    {
        var operation = await Seed();
        var tables = new[] { "LifecycleParticipantRegistryRevisions", "LifecycleParticipantRegistryBindings",
            "LifecycleParticipantRegistryActivation", "OrganisationLifecycleOperations", "OrganisationLifecycleParticipants" };
        foreach (var table in tables)
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => Runtime(Organisation,
                $"DELETE FROM public.\"{table}\""));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        foreach (var table in tables.Take(3))
        {
            Assert.NotNull(await Runtime(Organisation, $"SELECT count(*) FROM public.\"{table}\""));
            foreach (var sql in new[] { $"INSERT INTO public.\"{table}\" DEFAULT VALUES",
                $"UPDATE public.\"{table}\" SET \"{(table.EndsWith("Bindings") ? "RevisionId" : "Id")}\"=\"{(table.EndsWith("Bindings") ? "RevisionId" : "Id")}\"" })
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() => Runtime(Organisation, sql));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
        }
        var participantDenied = await Assert.ThrowsAsync<PostgresException>(() => Runtime(Organisation,
            "UPDATE public.\"OrganisationLifecycleParticipants\" SET \"Mandatory\"=\"Mandatory\""));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, participantDenied.SqlState);
        Assert.Equal(1, Convert.ToInt32(await Runtime(Organisation,
            "UPDATE public.\"OrganisationLifecycleParticipants\" SET \"State\"=\"State\" RETURNING 1")));
        Assert.NotNull(await Row(operation.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Competing_admissions_replay_or_conflict_without_a_second_active_operation(bool sameIdentity)
    {
        Assert.True(await Activate(Registry(RevisionOne, "participant-original"), 0));
        var barrier = new AdmissionReadBarrier();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(Connection("zeka_auth_runtime"))
            .AddInterceptors(barrier).Options;
        var firstKey = Guid.NewGuid();
        var secondKey = sameIdentity ? firstKey : Guid.NewGuid();
        Task<AdmissionResult> Admit(Guid key) => new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(options, TimeProvider.System, new FixtureExportInventory())).AdmitAsync(
                Guid.Parse("46000000-0000-0000-0000-000000000010"), Organisation, LifecycleOperationFamily.Export, key);
        var results = await Task.WhenAll(Admit(firstKey), Admit(secondKey));
        Assert.Equal(2, barrier.InitialReads);
        Assert.Single(results.Where(x => x.Status == AdmissionStatus.Admitted));
        Assert.Single(results.Where(x => x.Status == (sameIdentity ? AdmissionStatus.Replay : AdmissionStatus.Conflict)));
        Assert.Equal(results[0].Operation!.Id, results[1].Operation!.Id);
        Assert.Equal(1L, await Runtime(Organisation, "SELECT count(*) FROM public.\"OrganisationLifecycleOperations\""));
        var winner = results.Single(x => x.Status == AdmissionStatus.Admitted).Operation!;
        var replay = await Admit(winner.IdempotencyId);
        Assert.Equal(AdmissionStatus.Replay, replay.Status);
        Assert.Equal(winner.Id, replay.Operation!.Id);
        var conflict = await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(options, TimeProvider.System, new FixtureExportInventory()))
            .AdmitAsync(Guid.NewGuid(), Organisation, LifecycleOperationFamily.Export, winner.IdempotencyId);
        Assert.Equal(AdmissionStatus.Conflict, conflict.Status);
        Assert.Equal(1L, await Runtime(Organisation, "SELECT count(*) FROM public.\"OrganisationLifecycleOperations\""));
    }

    [Fact]
    public async Task Successor_changes_new_admission_only_and_unavailable_original_participant_stays_frozen()
    {
        var original = await Seed();
        var header = await Row(original.Id);
        var projection = await Runtime(Organisation,
            "SELECT json_agg(row_to_json(p) ORDER BY p.\"CapabilityKey\")::text FROM public.\"OrganisationLifecycleParticipants\" p");
        // The old participant has no host, connection or availability provider in this fixture.
        Assert.True(await Activate(Registry(RevisionTwo, "participant-successor"), 1));
        var nextOrg = Guid.Parse("46000000-0000-0000-0000-000000000002");
        var successor = await Admit(nextOrg, LifecycleOperationFamily.Export, Guid.NewGuid());
        Assert.Equal(AdmissionStatus.Admitted, successor.Status);
        Assert.Equal(RevisionTwo, successor.Operation!.RegistryRevision);
        Assert.Equal(Registry(RevisionTwo, "participant-successor").InventoryHash, successor.Operation.InventoryHash);
        Assert.Equal("participant-successor", Assert.Single(successor.Operation.Participants).ParticipantId);
        Assert.Equal(header, await Row(original.Id));
        Assert.Equal(projection, await Runtime(Organisation,
            "SELECT json_agg(row_to_json(p) ORDER BY p.\"CapabilityKey\")::text FROM public.\"OrganisationLifecycleParticipants\" p"));
        var replay = await Admit(Organisation, LifecycleOperationFamily.Export, original.IdempotencyId);
        Assert.Equal(AdmissionStatus.Replay, replay.Status);
        Assert.Equal(RevisionOne, replay.Operation!.RegistryRevision);
        var participant = Assert.Single(replay.Operation.Participants);
        Assert.Equal("participant-original", participant.ParticipantId);
        Assert.True(participant.Mandatory);
        Assert.Equal(LifecycleParticipantState.Pending, participant.State);
    }

    [Fact]
    public async Task Resume_after_successor_activation_awaits_the_unavailable_frozen_original_participant()
    {
        var originalRegistry = ExportRegistry(RevisionOne, "participant-original");
        Assert.True(await Activate(originalRegistry, 0));
        var admitted = await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options("zeka_auth_runtime"), TimeProvider.System,
                new FixtureExportInventory()))
            .AdmitAsync(Guid.Parse("46000000-0000-0000-0000-000000000010"), Organisation,
                LifecycleOperationFamily.Export, Guid.NewGuid());
        var operation = Assert.IsType<LifecycleOperation>(admitted.Operation);
        Assert.True(await Activate(ExportRegistry(RevisionTwo, "participant-successor"), 1));

        var store = new LifecycleExportStore(Options("zeka_auth_runtime"),
            new DeterministicExportPackageAssembler(), new InMemoryExportArtifactStore(),
            new InMemoryExportPackageSink(), new FixtureExportInventory(), TimeProvider.System);
        Assert.Equal(ExportProgressStatus.Progressed,
            (await store.ResumeAsync(operation.Id, Organisation, default)).Status);
        Assert.Equal(ExportProgressStatus.AwaitingParticipants,
            (await store.ResumeAsync(operation.Id, Organisation, default)).Status);

        Assert.Equal($"{(int)LifecycleOperationState.EnteringFence}|true|{RevisionOne:D}",
            await Runtime(Organisation,
                $"SELECT \"State\"::text || '|' || \"IsActive\"::text || '|' || \"RegistryRevision\"::text " +
                $"FROM public.\"OrganisationLifecycleOperations\" WHERE \"Id\"='{operation.Id:D}'"));
        var participants = (string)(await Runtime(Organisation,
            $"SELECT string_agg(DISTINCT \"ParticipantId\", ',' ORDER BY \"ParticipantId\") " +
            $"FROM public.\"OrganisationLifecycleParticipants\" WHERE \"OperationId\"='{operation.Id:D}'"))!;
        Assert.Equal("participant-original", participants);
        Assert.Equal(1L, await Runtime(Organisation,
            $"SELECT count(*) FROM public.\"OutboxMessages\" WHERE \"CorrelationId\"='{operation.Id:D}'"));
    }

    [Fact]
    public async Task Families_are_unique_and_export_is_denied_during_active_termination()
    {
        Assert.True(await Activate(Registry(RevisionOne, "participant-original"), 0));
        var close = await Admit(Organisation, LifecycleOperationFamily.Termination, Guid.NewGuid());
        Assert.Equal(AdmissionStatus.Admitted, close.Status);
        Assert.Equal(AdmissionStatus.Conflict, (await Admit(Organisation, LifecycleOperationFamily.Termination, Guid.NewGuid())).Status);
        Assert.Equal(AdmissionStatus.Conflict, (await Admit(Organisation, LifecycleOperationFamily.Export, Guid.NewGuid())).Status);
        Assert.Equal(1L, await Runtime(Organisation, "SELECT count(*) FROM public.\"OrganisationLifecycleOperations\""));
        var other = Guid.NewGuid();
        Assert.Equal(AdmissionStatus.Admitted, (await Admit(other, LifecycleOperationFamily.Export, Guid.NewGuid())).Status);
        Assert.Equal(AdmissionStatus.Admitted, (await Admit(other, LifecycleOperationFamily.Termination, Guid.NewGuid())).Status);
        Assert.Equal(2L, await Runtime(other, "SELECT count(*) FROM public.\"OrganisationLifecycleOperations\""));
    }

    [Fact]
    public async Task Registry_absence_fails_closed_and_activation_has_one_optimistic_winner()
    {
        Assert.Equal(AdmissionStatus.RegistryUnavailable,
            (await Admit(Organisation, LifecycleOperationFamily.Export, Guid.NewGuid())).Status);
        Assert.Equal(0L, await Runtime(Organisation, "SELECT count(*) FROM public.\"OrganisationLifecycleOperations\""));
        var outcomes = await Task.WhenAll(Activate(Registry(RevisionOne, "first"), 0), Activate(Registry(RevisionTwo, "second"), 0));
        Assert.Single(outcomes.Where(value => value));
        Assert.Single(outcomes.Where(value => !value));
        Assert.Equal(1L, await Sql(postgres.GetConnectionString(), "SELECT count(*) FROM public.\"LifecycleParticipantRegistryActivation\""));
        Assert.Equal(1L, await Sql(postgres.GetConnectionString(), "SELECT count(*) FROM public.\"LifecycleParticipantRegistryRevisions\""));
        await using var reader = Context("zeka_auth_runtime");
        var active = await new LifecycleRegistryReader(reader).ReadActiveAsync(default);
        Assert.NotNull(active);
        Assert.False(await Activate(Registry(Guid.NewGuid(), "stale"), 0));
        Assert.False(await Activate(active!, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("46000000000000000000000000000001")]
    public async Task Invalid_transaction_context_fails_closed(string? context)
    {
        await Seed();
        await using var connection = new NpgsqlConnection(Connection("zeka_auth_runtime"));
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (context is not null)
        {
            await using var set = new NpgsqlCommand("SELECT set_config('zeka.organisation_id',@value,true)", connection, transaction);
            set.Parameters.AddWithValue("value", context);
            await set.ExecuteScalarAsync();
        }
        await using var read = new NpgsqlCommand("SELECT * FROM public.\"OrganisationLifecycleOperations\"", connection, transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
    }

    [Fact]
    public async Task Runtime_insert_is_org_scoped_and_composite_participant_key_cannot_cross_organisations()
    {
        var original = await Seed();
        var other = Guid.Parse("46000000-0000-0000-0000-000000000002");
        Assert.Equal(0L, await Runtime(other, "SELECT count(*) FROM public.\"OrganisationLifecycleOperations\""));
        Assert.Equal(0L, await Runtime(other, "SELECT count(*) FROM public.\"OrganisationLifecycleParticipants\""));
        var insert = $"""
            INSERT INTO public."OrganisationLifecycleOperations"
            ("Id","OrganisationId","Family","State","RequestingSubjectId","IdempotencyId","RegistryRevision","InventoryHash","RequestedAt","Revision","IsActive")
            VALUES ('{Guid.NewGuid():D}','{other:D}',0,0,'{Guid.NewGuid():D}','{Guid.NewGuid():D}',
              '{RevisionOne:D}','{original.InventoryHash}',now(),1,true)
            """;
        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => Runtime(Organisation, insert));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, mismatch.SqlState);
        await Runtime(other, insert);
        var participantInsert = $"""
            INSERT INTO public."OrganisationLifecycleParticipants"
            ("OperationId","OrganisationId","Family","CapabilityKey","OwnershipScope","ParticipantId","ContractVersion","Mandatory","State")
            VALUES ('{original.Id:D}','{other:D}',0,'cross-org','fixture','original',1,true,0)
            """;
        var crossKey = await Assert.ThrowsAsync<PostgresException>(() => Runtime(other, participantInsert));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossKey.SqlState);
        var crossRls = await Assert.ThrowsAsync<PostgresException>(() => Runtime(Organisation, participantInsert));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, crossRls.SqlState);
        Assert.Equal(1L, await Runtime(Organisation, "SELECT count(*) FROM public.\"OrganisationLifecycleParticipants\""));
    }

    [Fact]
    public async Task Operational_organisation_removal_preserves_ledger_and_frozen_projection()
    {
        var operation = await Seed();
        await using (var owner = Context("zeka_auth_migrator"))
        {
            await owner.Database.OpenConnectionAsync();
            await owner.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            var user = AuthManager.Infrastructure.Identity.Models.User.Create("ledger@example.invalid",
                "synthetic-ledger-owner", "Synthetic", "Fixture", DateTimeOffset.UtcNow);
            owner.Users.Add(user);
            var organisation = AuthManager.Core.Organisations.Organisation.Create(Organisation, "Synthetic ledger survival", user.Id, DateTimeOffset.UtcNow);
            owner.Organisations.Add(organisation);
            await owner.SaveChangesAsync();
            owner.Organisations.Remove(organisation);
            await owner.SaveChangesAsync();
            Assert.False(await owner.Organisations.AnyAsync(x => x.Id == Organisation));
        }
        var replay = await Admit(Organisation, LifecycleOperationFamily.Export, operation.IdempotencyId);
        Assert.Equal(AdmissionStatus.Replay, replay.Status);
        Assert.Equal(operation.Id, replay.Operation!.Id);
        Assert.Equal(Organisation, replay.Operation.OrganisationId);
        Assert.Equal(operation.InventoryHash, replay.Operation.InventoryHash);
        Assert.Single(replay.Operation.Participants);
    }

    [Fact]
    public async Task Admission_freezes_complete_inventory_across_families_and_registry_validation_is_fail_closed()
    {
        var export = new LifecycleCapability(LifecycleOperationFamily.Export, "fixture.export-fragment", "fixture");
        var close = new LifecycleCapability(LifecycleOperationFamily.Termination, "closure", "fixture");
        LifecycleBinding[] bindings = [new(export, "owner-export", 1, true), new(close, "owner-close", 1, true)];
        LifecycleCapability[] required = [export, close];
        var invalid = new[]
        {
            Array.Empty<LifecycleBinding>(),
            new[] { bindings[0] },
            new[] { bindings[0], bindings[0], bindings[1] },
            new[] { bindings[0], bindings[0] with { ParticipantId = "overlap" }, bindings[1] },
            new[] { bindings[0] with { ContractVersion = 99 }, bindings[1] },
            new[] { bindings[0] with { Mandatory = false }, bindings[1] }
        };
        foreach (var candidate in invalid)
        {
            var invalidResult = LifecycleRegistry.Validate(Guid.NewGuid(), "synthetic-review", required, candidate);
            Assert.Null(invalidResult.Registry);
            Assert.NotNull(invalidResult.Error);
            Assert.Equal(0L, await Sql(postgres.GetConnectionString(), "SELECT count(*) FROM public.\"LifecycleParticipantRegistryActivation\""));
        }
        var registry = LifecycleRegistry.Validate(RevisionOne, "synthetic-review", required, bindings).Registry!;
        Assert.True(await Activate(registry, 0));
        var admitted = await Admit(Organisation, LifecycleOperationFamily.Export, Guid.NewGuid());
        Assert.Equal(AdmissionStatus.Admitted, admitted.Status);
        Assert.Equal(registry.InventoryHash, admitted.Operation!.InventoryHash);
        Assert.Equal(registry.Revision, admitted.Operation.RegistryRevision);
        Assert.Equal(2, admitted.Operation.Participants.Count);
        Assert.Equal(registry.Inventory.Select(x => x.ParticipantId).Order(), admitted.Operation.Participants.Select(x => x.ParticipantId).Order());
        var replay = await Admit(Organisation, LifecycleOperationFamily.Export, admitted.Operation.IdempotencyId);
        Assert.Equal(2, replay.Operation!.Participants.Count);
        Assert.All(replay.Operation.Participants, participant => Assert.True(participant.Mandatory));
    }

    [Fact]
    public async Task Manifest_rejects_unclassified_relations_RLS_cascade_and_forbidden_lifecycle_grants()
    {
        await using var verification = Context("zeka_auth_migrator");
        var mutations = new (string Mutation, string Restore, string Category)[]
        {
            ("CREATE TABLE public.unclassified_probe(id integer)", "DROP TABLE public.unclassified_probe", "catalog tables"),
            ("ALTER TABLE public.\"OrganisationLifecycleOperations\" DISABLE ROW LEVEL SECURITY",
                "ALTER TABLE public.\"OrganisationLifecycleOperations\" ENABLE ROW LEVEL SECURITY", "RLS state"),
            ("ALTER TABLE public.\"OrganisationLifecycleParticipants\" NO FORCE ROW LEVEL SECURITY",
                "ALTER TABLE public.\"OrganisationLifecycleParticipants\" FORCE ROW LEVEL SECURITY", "RLS state"),
            ("GRANT UPDATE ON public.\"LifecycleParticipantRegistryActivation\" TO zeka_auth_runtime",
                "REVOKE UPDATE ON public.\"LifecycleParticipantRegistryActivation\" FROM zeka_auth_runtime", "table ACLs"),
            ("GRANT DELETE ON public.\"OrganisationLifecycleOperations\" TO zeka_auth_runtime",
                "REVOKE DELETE ON public.\"OrganisationLifecycleOperations\" FROM zeka_auth_runtime", "table ACLs"),
            ("GRANT UPDATE ON public.\"OrganisationLifecycleParticipants\" TO zeka_auth_runtime",
                Bootstrap(), "table ACLs"),
            ("GRANT UPDATE (\"Mandatory\") ON public.\"LifecycleParticipantRegistryBindings\" TO zeka_auth_runtime",
                "REVOKE UPDATE (\"Mandatory\") ON public.\"LifecycleParticipantRegistryBindings\" FROM zeka_auth_runtime", "column ACLs"),
            ("ALTER TABLE public.\"OrganisationLifecycleOperations\" ADD CONSTRAINT unexpected_organisation_fk FOREIGN KEY (\"OrganisationId\") REFERENCES public.\"Organisations\"(\"Id\") ON DELETE CASCADE",
                "ALTER TABLE public.\"OrganisationLifecycleOperations\" DROP CONSTRAINT unexpected_organisation_fk", "lifecycle foreign keys")
        };
        foreach (var (mutation, restore, category) in mutations)
        {
            await Sql(postgres.GetConnectionString(), mutation);
            try
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly));
                Assert.Contains(category, failure.Message);
                output.WriteLine("Rejected: " + category + " / " + mutation);
            }
            finally { await Sql(postgres.GetConnectionString(), restore); }
            await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
        }
    }

    [Fact]
    public async Task Manifest_rejects_missing_duplicate_overlap_wildcard_and_wrong_subclass_inventories()
    {
        await using var verification = Context("zeka_auth_migrator");
        var baseline = RlsSecurityManifestVerifier.Load(typeof(AuthDbContext).Assembly);
        var global = baseline.GlobalControlPlaneTables!;
        var scoped = baseline.OrganisationScopedSurvivingControlTables!;
        var operation = new ManagedObjectIdentity("public", "OrganisationLifecycleOperations");
        var v10ColumnScoped = baseline with
        {
            SchemaVersion = 10,
            GlobalControlPlaneTables = null,
            OrganisationScopedSurvivingControlTables = null,
            ColumnPrivileges = [new(operation, "State", ["UPDATE"], false)]
        };
        var variants = new[]
        {
            baseline with { GlobalControlPlaneTables = null },
            baseline with { GlobalControlPlaneTables = global.Take(2).ToArray() },
            baseline with { GlobalControlPlaneTables = global.Concat([global[0]]).ToArray() },
            baseline with { GlobalControlPlaneTables = [new("public", "LifecycleParticipantRegistry*")] },
            baseline with { GlobalControlPlaneTables = scoped },
            baseline with { OrganisationScopedSurvivingControlTables = global },
            baseline with { OrganisationScopedSurvivingControlTables = null },
            baseline with { ExcludedTables = baseline.ExcludedTables.Except([global[0]]).ToArray() },
            baseline with { ProtectedTables = scoped.Concat([global[0]]).ToArray() },
            baseline with { ColumnPrivileges = null },
            baseline with { ColumnPrivileges = baseline.ColumnPrivileges!.Reverse().ToArray() },
            baseline with { ColumnPrivileges = [baseline.ColumnPrivileges![0], baseline.ColumnPrivileges[0]] },
            baseline with { ColumnPrivileges = [baseline.ColumnPrivileges![0] with { Column = "*" }, baseline.ColumnPrivileges[1]] },
            baseline with { ColumnPrivileges = [baseline.ColumnPrivileges![0] with { Grantable = true }, baseline.ColumnPrivileges[1]] },
            baseline with { RuntimeFunctionDefinitions = null },
            baseline with
            {
                RuntimeFunctionDefinitions =
                [baseline.RuntimeFunctionDefinitions![0], baseline.RuntimeFunctionDefinitions[0]]
            },
            baseline with
            {
                RuntimeFunctionDefinitions =
                [baseline.RuntimeFunctionDefinitions![0] with { DefinitionSha256 = "invalid" },
                    baseline.RuntimeFunctionDefinitions[1]]
            },
            v10ColumnScoped with { ColumnPrivileges = [new(operation, "*", ["UPDATE"], false)] },
            v10ColumnScoped with { ColumnPrivileges = [new(operation, "State", ["DELETE"], false)] },
            v10ColumnScoped with { ColumnPrivileges = [new(operation, "State", ["UPDATE"], true)] },
            v10ColumnScoped with
            {
                TablePrivileges = baseline.TablePrivileges.Select(state => state.Object == operation
                    ? state with { Privileges = ["INSERT", "SELECT", "UPDATE"] }
                    : state).ToArray()
            }
        };
        foreach (var variant in variants)
            await Assert.ThrowsAsync<InvalidOperationException>(() => RlsSecurityManifestVerifier.VerifyAsync(verification, variant));
        await RlsSecurityManifestVerifier.VerifyAsync(verification, baseline);
    }

    private Task<AdmissionResult> Admit(Guid organisation, LifecycleOperationFamily family, Guid identity) =>
        new LifecycleAdmission(new CurrentAccess(), new LifecycleAdmissionStore(Options("zeka_auth_runtime"),
            TimeProvider.System, new FixtureExportInventory()))
            .AdmitAsync(Guid.Parse("46000000-0000-0000-0000-000000000010"), organisation, family, identity);

    private sealed class AdmissionReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public int InitialReads => Math.Min(reads, 2);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"OrganisationLifecycleOperations\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref reads) <= 2)
            {
                if (reads == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private async Task<LifecycleOperation> Seed()
    {
        Assert.True(await Activate(Registry(RevisionOne, "participant-original"), 0));
        var result = await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options("zeka_auth_runtime"), TimeProvider.System,
                new FixtureExportInventory()))
            .AdmitAsync(Guid.Parse("46000000-0000-0000-0000-000000000010"), Organisation,
                LifecycleOperationFamily.Export, Guid.NewGuid());
        Assert.Equal(AdmissionStatus.Admitted, result.Status);
        return Assert.IsType<LifecycleOperation>(result.Operation);
    }

    private Task<object?> Row(Guid id) => Runtime(Organisation,
        $"SELECT row_to_json(operation)::text FROM public.\"OrganisationLifecycleOperations\" operation WHERE \"Id\"='{id:D}'");

    private async Task<object?> Runtime(Guid organisation, string sql)
    {
        await using var connection = new NpgsqlConnection(Connection("zeka_auth_runtime"));
        await connection.OpenAsync();
        Assert.Equal("zeka_auth_runtime", await new NpgsqlCommand("SELECT session_user", connection).ExecuteScalarAsync());
        await using var transaction = await connection.BeginTransactionAsync();
        await using var context = new NpgsqlCommand("SELECT pg_catalog.set_config('zeka.organisation_id',@organisation,true)", connection, transaction);
        context.Parameters.AddWithValue("organisation", organisation.ToString("D"));
        await context.ExecuteScalarAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result;
    }

    private async Task<bool> Activate(LifecycleRegistry registry, long expectedVersion)
    {
        await using var database = Context("zeka_auth_migrator");
        await database.Database.OpenConnectionAsync();
        await database.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
        return await new ReviewedLifecycleRegistryActivation(database).ActivateAsync(registry, expectedVersion, default);
    }

    private static LifecycleRegistry Registry(Guid revision, string participant)
    {
        var capability = new LifecycleCapability(LifecycleOperationFamily.Export,
            "fixture.export-fragment", "fixture-owned-records");
        var validated = LifecycleRegistry.Validate(revision, "Issue46-LIFE-FND-01-fixture-v1",
            [capability], [new LifecycleBinding(capability, participant, 1, true)]);
        Assert.Null(validated.Error);
        return Assert.IsType<LifecycleRegistry>(validated.Registry);
    }

    private static LifecycleRegistry ExportRegistry(Guid revision, string participant)
    {
        var fence = new LifecycleCapability(LifecycleOperationFamily.Export,
            OrganisationExportCapabilityV1.Fence, "fixture-owned-records");
        var fragment = new LifecycleCapability(LifecycleOperationFamily.Export,
            "fixture.export-fragment", "fixture-owned-records");
        var bindings = new[]
        {
            new LifecycleBinding(fence, participant, 1, true),
            new LifecycleBinding(fragment, participant, 1, true)
        };
        var validated = LifecycleRegistry.Validate(revision, "Issue46-LIFE-01-resume-v1",
            [fence, fragment], bindings);
        Assert.Null(validated.Error);
        return Assert.IsType<LifecycleRegistry>(validated.Registry);
    }

    private sealed class CurrentAccess : ICurrentTenantAccess
    {
        public Task<CurrentTenantAccess> ResolveAsync(Guid subject, Guid organisation, CancellationToken cancellationToken) =>
            Task.FromResult(new CurrentTenantAccess(TenantAccessOutcome.Authorized, organisation,
                Guid.Parse("46000000-0000-0000-0000-000000000030"), [LifecyclePermissions.Export, LifecyclePermissions.Close],
                "synthetic-authorized-membership", DateTimeOffset.UtcNow));
    }

    private sealed class FixtureExportInventory : IReviewedExportCategoryInventory
    {
        private static readonly IReadOnlyList<ReviewedExportCategoryRequirement> Requirements =
            [new("records", new HashSet<ExportCategoryDispositionV1>
                { ExportCategoryDispositionV1.Included, ExportCategoryDispositionV1.Empty })];

        public IReadOnlyList<ReviewedExportCategoryRequirement> RequirementsFor(string participantId) => Requirements;

        public LifecycleExportInventory Freeze(LifecycleRegistry registry) => LifecycleExportInventory.Create(registry,
            registry.Inventory.Where(x => x.Capability.Family == LifecycleOperationFamily.Export
                    && x.Capability.Key.EndsWith(".export-fragment", StringComparison.Ordinal))
                .Select(x => new LifecycleExportCategoryRequirement(x.ParticipantId, "records", ["empty", "included"])));
    }

    private string Connection(string role) => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = role, Password = FixturePassword, Pooling = false }.ConnectionString;
    private DbContextOptions<AuthDbContext> Options(string role) =>
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(Connection(role)).Options;
    private AuthDbContext Context(string role) => new(Options(role));

    private static async Task<object?> Sql(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static string Bootstrap()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-auth-roles.sql");
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Accepted AuthManagement bootstrap not found.");
    }
}

[Trait("Issue", "46")]
[Trait("Evidence", "LIFE-FND-01")]
public sealed class LifecycleFoundationContractTests
{
    [Fact]
    public void Registry_hash_is_golden_order_independent_and_immutable()
    {
        var capability = new LifecycleCapability(LifecycleOperationFamily.Export, "records", "fixture-owned-records");
        var binding = new LifecycleBinding(capability, "participant-original", 1, true);
        var revision = Guid.Parse("46000000-0000-0000-0000-000000000101");
        var original = LifecycleRegistry.Validate(revision, "synthetic-review", [capability], [binding]).Registry!;
        Assert.Equal("abedb6e3a9fc0906583213679a4011e241bc2e5cfe130bef1a006bf4c2c7b25c", original.InventoryHash);
        Assert.Equal("[[0,\"records\",\"fixture-owned-records\",\"participant-original\",1,true]]", original.InventoryJson);
        var close = new LifecycleCapability(LifecycleOperationFamily.Termination, "closure", "fixture-owned-records");
        var mutableInput = new[] { binding, new LifecycleBinding(close, "close-owner", 1, true) };
        var first = LifecycleRegistry.Validate(revision, "review", [close, capability], mutableInput).Registry!;
        var second = LifecycleRegistry.Validate(Guid.NewGuid(), "new-review", [capability, close], mutableInput.Reverse()).Registry!;
        Assert.Equal(first.InventoryHash, second.InventoryHash);
        mutableInput[0] = binding with { ParticipantId = "different-owner" };
        Assert.Equal("participant-original", first.Inventory[0].ParticipantId);
        Assert.NotEqual(first.InventoryHash,
            LifecycleRegistry.Validate(revision, "review", [close, capability], mutableInput).Registry!.InventoryHash);
        Assert.Throws<NotSupportedException>(() => ((IList<LifecycleBinding>)first.Inventory)[0] = mutableInput[0]);
    }

    [Theory]
    [InlineData("missing", RegistryValidationError.MissingOwner)]
    [InlineData("overlap", RegistryValidationError.OverlappingOwner)]
    [InlineData("duplicate", RegistryValidationError.DuplicateBinding)]
    [InlineData("unsupported", RegistryValidationError.UnsupportedContract)]
    [InlineData("incomplete", RegistryValidationError.IncompleteInventory)]
    public void Invalid_registry_has_no_activatable_revision(string defect, RegistryValidationError expected)
    {
        var capability = new LifecycleCapability(LifecycleOperationFamily.Export, "records", "fixture");
        var binding = new LifecycleBinding(capability, "owner", 1, true);
        LifecycleBinding[] inventory = defect switch
        {
            "missing" => [],
            "overlap" => [binding, binding with { ParticipantId = "other" }],
            "duplicate" => [binding, binding],
            "unsupported" => [binding with { ContractVersion = 2 }],
            _ => [binding with { Mandatory = false }]
        };
        var result = LifecycleRegistry.Validate(Guid.NewGuid(), "review", [capability], inventory);
        Assert.Null(result.Registry);
        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData(LifecycleOperationFamily.Export, LifecyclePermissions.Export)]
    [InlineData(LifecycleOperationFamily.Termination, LifecyclePermissions.Close)]
    public async Task Admission_requires_current_matching_membership_and_exact_permission(LifecycleOperationFamily family, string permission)
    {
        var org = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var valid = new CurrentTenantAccess(TenantAccessOutcome.Authorized, org, Guid.NewGuid(), [permission], "v1", DateTimeOffset.UtcNow);
        var access = new ObservedAccess(valid);
        var store = new RecordingAdmission();
        var application = new LifecycleAdmission(access, store);
        Assert.Equal(AdmissionStatus.Admitted, (await application.AdmitAsync(subject, org, family, Guid.NewGuid())).Status);
        Assert.Equal(subject, store.Last!.SubjectId);
        Assert.Equal(org, store.Last.OrganisationId);
        Assert.Equal(family, store.Last.Family);
        foreach (var denied in new[]
        {
            valid with { Outcome = TenantAccessOutcome.Denied },
            valid with { OrganisationId = Guid.NewGuid() },
            valid with { OrganisationMembershipId = Guid.Empty },
            valid with { EffectivePermissionCodes = [] },
            valid with { EffectivePermissionCodes = ["Owner", "Admin"] },
            valid with { EffectivePermissionCodes = [family == LifecycleOperationFamily.Export ? LifecyclePermissions.Close : LifecyclePermissions.Export] }
        })
        {
            access.Value = denied;
            Assert.Equal(AdmissionStatus.Denied, (await application.AdmitAsync(subject, org, family, Guid.NewGuid())).Status);
            Assert.Equal(1, store.Calls);
        }
        access.Value = valid with { Outcome = TenantAccessOutcome.Unavailable };
        Assert.Equal(AdmissionStatus.Unavailable, (await application.AdmitAsync(subject, org, family, Guid.NewGuid())).Status);
        Assert.Equal(1, store.Calls);
        Assert.Equal(8, access.Calls); // each request re-observes current access, including revocation/outage
    }

    [Fact]
    public void Operational_status_and_lifecycle_admission_remain_distinct_without_participant_dependencies()
    {
        var org = AuthManager.Core.Organisations.Organisation.Create(Guid.NewGuid(), "fixture", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var status = org.Status;
        var capability = new LifecycleCapability(LifecycleOperationFamily.Export, "records", "fixture");
        var registry = LifecycleRegistry.Validate(Guid.NewGuid(), "review", [capability], [new(capability, "owner", 1, true)]).Registry!;
        var operation = LifecycleOperation.Admit(Guid.NewGuid(), org.Id, Guid.NewGuid(), LifecycleOperationFamily.Termination,
            Guid.NewGuid(), registry, DateTimeOffset.UtcNow);
        Assert.Equal(status, org.Status);
        Assert.Equal(LifecycleOperationState.Pending, operation.State);
        Assert.NotEqual(typeof(AuthManager.Core.Organisations.OrganisationStatus), operation.State.GetType());
        foreach (var assembly in new[] { typeof(LifecycleOperation).Assembly, typeof(LifecycleAdmission).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name!.Contains("AdminArea", StringComparison.Ordinal)
                || reference.Name.Contains("ClientManagement", StringComparison.Ordinal)
                || reference.Name.Contains("Infrastructure", StringComparison.Ordinal)
                || reference.Name.StartsWith("Npgsql", StringComparison.Ordinal)
                || reference.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(typeof(LifecycleAdmission).GetConstructors().SelectMany(x => x.GetParameters()),
            parameter => parameter.ParameterType == typeof(IReviewedLifecycleRegistryActivation));
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        configuration["ConnectionStrings:Default"] = "Host=localhost;Database=synthetic;Username=zeka_auth_runtime";
        AuthManager.Infrastructure.DependencyInjection.Infrastructure(services, configuration);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ILifecycleRegistryReader));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IReviewedLifecycleRegistryActivation)
            || descriptor.ImplementationType == typeof(ReviewedLifecycleRegistryActivation));
    }

    private sealed class ObservedAccess(CurrentTenantAccess value) : ICurrentTenantAccess
    {
        public CurrentTenantAccess Value { get; set; } = value;
        public int Calls { get; private set; }
        public Task<CurrentTenantAccess> ResolveAsync(Guid subject, Guid organisation, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Value); }
    }
    private sealed class RecordingAdmission : ILifecycleAdmissionStore
    {
        public int Calls { get; private set; }
        public AuthorizedLifecycleAdmission? Last { get; private set; }
        public Task<AdmissionResult> AdmitAsync(AuthorizedLifecycleAdmission request, CancellationToken cancellationToken)
        { Calls++; Last = request; return Task.FromResult(new AdmissionResult(AdmissionStatus.Admitted)); }
    }
}

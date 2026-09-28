using System.Text;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Lifecycle;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Infrastructure.Persistence.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "ProviderReal")]
public sealed class ClientOrganisationExportParticipantEvidenceTests(PostgreSqlClientRuntimeDatabase database)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    [Fact]
    public async Task Client_export_fence_triggers_cover_every_frozen_write_surface()
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT trigger.tgname FROM pg_catalog.pg_trigger trigger
            JOIN pg_catalog.pg_class relation ON relation.oid = trigger.tgrelid
            JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
            WHERE NOT trigger.tgisinternal AND schema.nspname = 'public'
              AND trigger.tgname LIKE 'trg_%_export_fence' ORDER BY trigger.tgname
            """, connection);
        var actual = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) actual.Add(reader.GetString(0));
        Assert.Equal(new[]
        {
            "trg_assessments_export_fence", "trg_clients_export_fence",
            "trg_monitoringreports_export_fence", "trg_professionalassessments_export_fence",
            "trg_professionnalexperience_export_fence", "trg_schoolregistrations_export_fence",
            "trg_socialcases_export_fence", "trg_socialworkers_export_fence"
        }, actual);
    }

    [Fact]
    public async Task Client_export_is_deterministic_tenant_isolated_soft_delete_accounted_and_write_fenced()
    {
        var organisation = Guid.NewGuid();
        var otherOrganisation = Guid.NewGuid();
        using var artifacts = new TemporaryArtifacts();
        await using var provider = Provider(organisation, artifacts.Root);
        await SeedSyntheticClient(provider, organisation, 21, softDeleted: true);

        var operation = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var enter = new EnterOrganisationExportFenceV1(Header(operation, organisation, Guid.NewGuid(), correlation));
        var entered = await Invoke(provider, organisation, participant => participant.EnterFenceAsync(enter));
        var enteredReplay = await Invoke(provider, organisation, participant => participant.EnterFenceAsync(enter));
        Assert.Equal(entered.ReceiptHash, enteredReplay.ReceiptHash);
        Assert.Equal(entered.Header.MessageId, enteredReplay.Header.MessageId);

        var blocked = await Assert.ThrowsAsync<DbUpdateException>(() =>
            SeedSyntheticClient(provider, organisation, 22, softDeleted: false));
        Assert.Equal("organisation_export_fence_active",
            Assert.IsType<PostgresException>(blocked.InnerException).MessageText);
        var backgroundBlocked = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSql(organisation,
            "UPDATE public.\"Clients\" SET \"ReferenceNumber\" = \"ReferenceNumber\""));
        Assert.Equal("organisation_export_fence_active", backgroundBlocked.MessageText);
        await SeedSyntheticClient(provider, otherOrganisation, 23, softDeleted: false);

        var stage = Stage(operation, organisation, correlation, entered);
        var ready = await Invoke(provider, organisation, participant => participant.StageAsync(stage));
        var replay = await Invoke(provider, organisation, participant => participant.StageAsync(stage));
        Assert.Equal(ready.FragmentHash, replay.FragmentHash);
        Assert.Equal(ready.Header.MessageId, replay.Header.MessageId);
        Assert.Equal(ClientExportContract.Categories, ready.Categories.Select(x => x.Category));
        Assert.Equal(1, ready.Categories.Single(x => x.Category == "beneficiaries").RecordCount);
        Assert.Equal(ExportCategoryDispositionV1.Withheld,
            ready.Categories.Single(x => x.Category == "notes").Disposition);
        foreach (var category in new[] { "generated-report-artifacts", "audit-events", "integration-events" })
            Assert.Equal(ExportCategoryDispositionV1.NotImplemented,
                ready.Categories.Single(x => x.Category == category).Disposition);

        await InTenant(provider, organisation, async services =>
        {
            await services.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
            {
                var persisted = await services.GetRequiredService<ApplicationDbContext>().OrganisationExportFragments
                    .AsNoTracking().SingleAsync(x => x.OperationId == operation && x.Category == "beneficiaries", token);
                Assert.Equal(1, persisted.SoftDeletedRecordCount);
            }, default);
        });

        var beneficiary = ready.Categories.Single(x => x.Category == "beneficiaries");
        var file = Assert.Single(Directory.GetFiles(artifacts.Root, "beneficiaries.csv.*.artifact", SearchOption.AllDirectories));
        var content = await File.ReadAllBytesAsync(file);
        Assert.Equal(beneficiary.ContentSha256, ClientExportCanonical.Sha256(content));
        Assert.Contains("synthetic-reference", Encoding.UTF8.GetString(content), StringComparison.Ordinal);
        Assert.DoesNotContain(otherOrganisation.ToString("D"), Encoding.UTF8.GetString(content), StringComparison.OrdinalIgnoreCase);

        await Invoke(provider, organisation, participant => participant.ReleaseFenceAsync(
            new ReleaseOrganisationExportFenceV1(Header(operation, organisation, Guid.NewGuid(), correlation), entered.FenceToken)));
        await SeedSyntheticClient(provider, organisation, 24, softDeleted: false);
    }

    [Fact]
    public async Task Fixture_registration_fails_closed_and_artifact_set_rejects_tamper_and_extra_files()
    {
        var fixtureOrganisation = Guid.NewGuid();
        using var artifacts = new TemporaryArtifacts();
        await using (var production = ProductionProvider())
            Assert.Null(production.GetService<IClientOrganisationExportParticipant>());
        await using var provider = Provider(fixtureOrganisation, artifacts.Root);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, Guid.NewGuid(),
            participant => participant.EnterFenceAsync(new(Header(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())))));
        Assert.Equal("client_export_nonfixture_scope_rejected", rejected.Message);

        var scope = new ClientExportFixtureScope(fixtureOrganisation);
        var store = new DeterministicClientExportArtifactStore(artifacts.Root, scope);
        var operation = Guid.NewGuid();
        var receipt = await store.WriteVerifiedAsync(fixtureOrganisation, operation,
            new ClientExportArtifactWrite("evidence", "fixture.csv", Encoding.UTF8.GetBytes("a,b\n1,2\n")), default);
        await store.VerifyExactAsync(fixtureOrganisation, operation, "evidence", [receipt], default);
        var file = Assert.Single(Directory.GetFiles(artifacts.Root, "*.artifact", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(file, "tampered");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.VerifyExactAsync(fixtureOrganisation, operation, "evidence", [receipt], default));
        File.Delete(file);
        receipt = await store.WriteVerifiedAsync(fixtureOrganisation, operation,
            new ClientExportArtifactWrite("evidence", "fixture.csv", Encoding.UTF8.GetBytes("a,b\n1,2\n")), default);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(file)!, "extra.artifact"), "extra");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.VerifyExactAsync(fixtureOrganisation, operation, "evidence", [receipt], default));
    }

    [Fact]
    public async Task Concurrent_stage_and_fresh_provider_restart_replay_the_same_immutable_receipt()
    {
        var organisation = Guid.NewGuid();
        using var artifacts = new TemporaryArtifacts();
        await using var providerA = Provider(organisation, artifacts.Root);
        await using var providerB = Provider(organisation, artifacts.Root);
        var operation = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var entered = await Invoke(providerA, organisation, participant => participant.EnterFenceAsync(
            new(Header(operation, organisation, Guid.NewGuid(), correlation))));
        var stage = Stage(operation, organisation, correlation, entered);
        var responses = await Task.WhenAll(
            Invoke(providerA, organisation, participant => participant.StageAsync(stage)),
            Invoke(providerB, organisation, participant => participant.StageAsync(stage)));
        Assert.Equal(responses[0].Header.MessageId, responses[1].Header.MessageId);
        Assert.Equal(responses[0].FragmentHash, responses[1].FragmentHash);

        await using var restarted = Provider(organisation, artifacts.Root);
        var replay = await Invoke(restarted, organisation, participant => participant.StageAsync(stage));
        Assert.Equal(responses[0].Header.MessageId, replay.Header.MessageId);
        Assert.Equal(responses[0].FragmentHash, replay.FragmentHash);
    }

    [Fact]
    public async Task Concurrent_duplicate_enter_and_release_return_the_durable_receipts()
    {
        var organisation = Guid.NewGuid();
        using var artifacts = new TemporaryArtifacts();
        await using var providerA = Provider(organisation, artifacts.Root);
        await using var providerB = Provider(organisation, artifacts.Root);
        var operation = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var enter = new EnterOrganisationExportFenceV1(
            Header(operation, organisation, Guid.NewGuid(), correlation));

        var entered = await Task.WhenAll(
            Invoke(providerA, organisation, participant => participant.EnterFenceAsync(enter)),
            Invoke(providerB, organisation, participant => participant.EnterFenceAsync(enter)));
        Assert.Equal(entered[0].Header.MessageId, entered[1].Header.MessageId);
        Assert.Equal(entered[0].ReceiptHash, entered[1].ReceiptHash);

        var release = new ReleaseOrganisationExportFenceV1(
            Header(operation, organisation, Guid.NewGuid(), correlation), entered[0].FenceToken);
        var released = await Task.WhenAll(
            Invoke(providerA, organisation, participant => participant.ReleaseFenceAsync(release)),
            Invoke(providerB, organisation, participant => participant.ReleaseFenceAsync(release)));
        Assert.Equal(released[0].Header.MessageId, released[1].Header.MessageId);
        Assert.Equal(released[0].ReleasedAt, released[1].ReleasedAt);
    }

    [Fact]
    public async Task Runtime_lifecycle_evidence_is_append_only_except_for_fence_release()
    {
        var organisation = Guid.NewGuid();
        using var artifacts = new TemporaryArtifacts();
        await using var provider = Provider(organisation, artifacts.Root);
        var operation = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var entered = await Invoke(provider, organisation, participant => participant.EnterFenceAsync(
            new(Header(operation, organisation, Guid.NewGuid(), correlation))));
        await Invoke(provider, organisation, participant => participant.StageAsync(
            Stage(operation, organisation, correlation, entered)));

        foreach (var sql in new[]
        {
            "UPDATE public.\"OrganisationExportFragments\" SET \"RecordCount\" = 99",
            "DELETE FROM public.\"OrganisationExportInbox\"",
            "UPDATE public.\"OrganisationExportOutbox\" SET \"PublishedAt\" = now()",
            "UPDATE public.\"OrganisationExportFences\" SET \"FenceToken\" = 'forged'"
        })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => RuntimeSql(organisation, sql))).SqlState);

        await Invoke(provider, organisation, participant => participant.ReleaseFenceAsync(
            new ReleaseOrganisationExportFenceV1(Header(operation, organisation, Guid.NewGuid(), correlation), entered.FenceToken)));
    }

    [Fact]
    public async Task Client_export_rejects_stale_or_forged_fence_evidence()
    {
        var organisation = Guid.NewGuid();
        using var artifacts = new TemporaryArtifacts();
        await using var provider = Provider(organisation, artifacts.Root);
        var operation = Guid.NewGuid();
        var entered = await Invoke(provider, organisation, participant => participant.EnterFenceAsync(
            new(Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid()))));
        var forgedHeader = new LifecycleMessageHeaderV1(operation, organisation, 1,
            ClientExportContract.ParticipantId, LifecycleContractV1.Version,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var forged = new OrganisationExportFenceEnteredV1(forgedHeader, "forged-token", 1, entered.EnteredAt);
        var evidence = new CompleteExportFenceEvidenceV1(Guid.NewGuid(), new string('b', 64),
            [new ExportFenceParticipantRequirementV1(ClientExportContract.ParticipantId, LifecycleContractV1.Version)],
            [new ExportFenceReceiptV1(forged)]);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
            participant => participant.StageAsync(new StageOrganisationExportV1(
                Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid()), entered.EnteredAt.AddTicks(1), evidence))));
        Assert.Equal("organisation_export_fence_evidence_stale", exception.Message);
    }

    private ServiceProvider Provider(Guid fixtureOrganisation, string artifactRoot)
    {
        var services = ProductionServices();
        services.AddClientExportFixtureParticipant(fixtureOrganisation, artifactRoot);
        return services.BuildServiceProvider();
    }

    private ServiceProvider ProductionProvider() => ProductionServices().BuildServiceProvider();

    private IServiceCollection ProductionServices()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/",
            ["FileServerPath"] = Path.GetTempPath()
        });
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        services.RemoveAll<IHostedService>();
        return services;
    }

    private async Task RuntimeSql(Guid organisation, string sql)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var context = new NpgsqlCommand("SELECT set_config('zeka.organisation_id', @organisation, true)", connection, transaction);
        context.Parameters.AddWithValue("organisation", organisation.ToString("D"));
        await context.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedSyntheticClient(IServiceProvider provider, Guid organisation, int sequence,
        bool softDeleted)
    {
        await InTenant(provider, organisation, services =>
            services.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
            {
                var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                    ClientManagement.Tests.Common.SyntheticClient.Niss(sequence));
                client.ReferenceNumber = $"synthetic-reference-{sequence:D2}";
                client.Softdelete = softDeleted;
                services.GetRequiredService<ApplicationDbContext>().Clients.Add(client);
                await services.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(token);
            }, default));
    }

    private static async Task<T> Invoke<T>(IServiceProvider provider, Guid organisation,
        Func<IClientOrganisationExportParticipant, Task<T>> action)
    {
        T result = default!;
        await InTenant(provider, organisation, async services =>
            result = await action(services.GetRequiredService<IClientOrganisationExportParticipant>()));
        return result;
    }

    private static async Task InTenant(IServiceProvider provider, Guid organisation,
        Func<IServiceProvider, Task> action)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "life-01-evidence"));
        await action(scope.ServiceProvider);
    }

    private static StageOrganisationExportV1 Stage(Guid operation, Guid organisation, Guid correlation,
        OrganisationExportFenceEnteredV1 entered)
    {
        var evidence = new CompleteExportFenceEvidenceV1(Guid.NewGuid(), new string('a', 64),
            [new ExportFenceParticipantRequirementV1(ClientExportContract.ParticipantId, LifecycleContractV1.Version)],
            [new ExportFenceReceiptV1(entered)]);
        return new StageOrganisationExportV1(Header(operation, organisation, Guid.NewGuid(), correlation),
            entered.EnteredAt.AddTicks(1), evidence);
    }

    private static LifecycleMessageHeaderV1 Header(Guid operation, Guid organisation, Guid message, Guid correlation) =>
        new(operation, organisation, 1, ClientExportContract.ParticipantId, LifecycleContractV1.Version,
            message, Guid.NewGuid(), correlation);

    private sealed class TemporaryArtifacts : IDisposable
    {
        public TemporaryArtifacts() => Root = Path.Combine(Path.GetTempPath(), $"zeka-client-export-{Guid.NewGuid():N}");
        public string Root { get; }
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

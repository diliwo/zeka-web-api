using System.Collections.Concurrent;
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
            SELECT trigger.tgname
            FROM pg_catalog.pg_trigger trigger
            JOIN pg_catalog.pg_class relation ON relation.oid = trigger.tgrelid
            JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
            WHERE NOT trigger.tgisinternal
              AND schema.nspname = 'public'
              AND trigger.tgname LIKE 'trg_%_export_fence'
            ORDER BY trigger.tgname
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
    public async Task Client_export_is_deterministic_replayable_tenant_isolated_and_write_fenced()
    {
        var organisationA = Guid.NewGuid();
        var organisationB = Guid.NewGuid();
        var artifacts = new MemoryArtifactStore();
        await using var provider = Provider(artifacts);

        await InTenant(provider, organisationA, async services =>
        {
            await services.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
            {
                var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                    ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: 121));
                client.Softdelete = true;
                services.GetRequiredService<ApplicationDbContext>().Clients.Add(client);
                await services.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(token);
            }, default);
        });

        var operation = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        OrganisationExportFenceEnteredV1 entered = null!;
        await InTenant(provider, organisationA, async services =>
        {
            var participant = services.GetRequiredService<IClientOrganisationExportParticipant>();
            var enter = new EnterOrganisationExportFenceV1(Header(operation, organisationA, Guid.NewGuid(), correlation));
            entered = await participant.EnterFenceAsync(enter);
            var replay = await participant.EnterFenceAsync(enter);
            Assert.Equal(entered.ReceiptHash, replay.ReceiptHash);
            Assert.Equal(entered.Header.MessageId, replay.Header.MessageId);

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() =>
                services.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
                {
                    var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                        ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: 122));
                    services.GetRequiredService<ApplicationDbContext>().Clients.Add(client);
                    await services.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(token);
                }, default));
            Assert.Equal("organisation_export_fence_active", Assert.IsType<PostgresException>(exception.InnerException).MessageText);
        });

        // A fence is organisation-local: a second tenant remains writable.
        await InTenant(provider, organisationB, services =>
            services.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
            {
                var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                    ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: 123));
                services.GetRequiredService<ApplicationDbContext>().Clients.Add(client);
                await services.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(token);
            }, default));

        OrganisationExportFragmentReadyV1 ready = null!;
        await InTenant(provider, organisationA, async services =>
        {
            var participant = services.GetRequiredService<IClientOrganisationExportParticipant>();
            var evidence = new CompleteExportFenceEvidenceV1(Guid.NewGuid(), new string('a', 64),
                [new ExportFenceParticipantRequirementV1(ClientExportContract.ParticipantId, LifecycleContractV1.Version)],
                [new ExportFenceReceiptV1(entered)]);
            var stage = new StageOrganisationExportV1(
                Header(operation, organisationA, Guid.NewGuid(), correlation), entered.EnteredAt.AddTicks(1), evidence);
            ready = await participant.StageAsync(stage);
            var replay = await participant.StageAsync(stage);
            Assert.Equal(ready.FragmentHash, replay.FragmentHash);
            Assert.Equal(ready.Header.MessageId, replay.Header.MessageId);

            Assert.Equal(ClientExportContract.Categories, ready.Categories.Select(x => x.Category));
            Assert.Equal(ExportCategoryDispositionV1.Included,
                ready.Categories.Single(x => x.Category == "beneficiaries").Disposition);
            Assert.Equal(1, ready.Categories.Single(x => x.Category == "beneficiaries").RecordCount);
            Assert.Equal(ExportCategoryDispositionV1.Withheld,
                ready.Categories.Single(x => x.Category == "notes").Disposition);
            foreach (var category in new[] { "generated-report-artifacts", "audit-events", "integration-events" })
                Assert.Equal(ExportCategoryDispositionV1.NotImplemented,
                    ready.Categories.Single(x => x.Category == category).Disposition);

            await services.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
            {
                var persisted = await services.GetRequiredService<ApplicationDbContext>().OrganisationExportFragments
                    .AsNoTracking().SingleAsync(x => x.OperationId == operation && x.Category == "beneficiaries", token);
                Assert.Equal(1, persisted.SoftDeletedRecordCount);
            }, default);

            var release = new ReleaseOrganisationExportFenceV1(
                Header(operation, organisationA, Guid.NewGuid(), correlation), entered.FenceToken);
            var released = await participant.ReleaseFenceAsync(release);
            Assert.Equal(entered.FenceToken, released.FenceToken);
        });

        await InTenant(provider, organisationA, services =>
            services.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
            {
                var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                    ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: 124));
                client.ReferenceNumber = "synthetic-reference-after-release";
                services.GetRequiredService<ApplicationDbContext>().Clients.Add(client);
                await services.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(token);
            }, default));

        var beneficiary = ready.Categories.Single(x => x.Category == "beneficiaries");
        var content = artifacts.Read(beneficiary.ArtifactReference!);
        Assert.Equal(beneficiary.ContentSha256, ClientExportCanonical.Sha256(content));
        Assert.Contains("synthetic-reference", Encoding.UTF8.GetString(content), StringComparison.Ordinal);
        Assert.DoesNotContain(organisationB.ToString("D"), Encoding.UTF8.GetString(content), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Client_export_rejects_stale_or_forged_fence_evidence()
    {
        var organisation = Guid.NewGuid();
        await using var provider = Provider(new MemoryArtifactStore());
        await InTenant(provider, organisation, async services =>
        {
            var participant = services.GetRequiredService<IClientOrganisationExportParticipant>();
            var operation = Guid.NewGuid();
            var entered = await participant.EnterFenceAsync(new(Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid())));
            var forgedHeader = new LifecycleMessageHeaderV1(operation, organisation, 1,
                ClientExportContract.ParticipantId, LifecycleContractV1.Version,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var forgedEntered = new OrganisationExportFenceEnteredV1(forgedHeader, "forged-token", 1, entered.EnteredAt);
            var evidence = new CompleteExportFenceEvidenceV1(Guid.NewGuid(), new string('b', 64),
                [new ExportFenceParticipantRequirementV1(ClientExportContract.ParticipantId, LifecycleContractV1.Version)],
                [new ExportFenceReceiptV1(forgedEntered)]);
            var stage = new StageOrganisationExportV1(Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid()),
                entered.EnteredAt.AddTicks(1), evidence);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => participant.StageAsync(stage));
            Assert.Equal("organisation_export_fence_evidence_stale", exception.Message);
        });
    }

    private ServiceProvider Provider(MemoryArtifactStore artifacts)
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
        services.AddSingleton<IClientExportArtifactStore>(artifacts);
        return services.BuildServiceProvider();
    }

    private static async Task InTenant(IServiceProvider provider, Guid organisation,
        Func<IServiceProvider, Task> action)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "life-01-evidence"));
        await action(scope.ServiceProvider);
    }

    private static LifecycleMessageHeaderV1 Header(Guid operation, Guid organisation, Guid message, Guid correlation) =>
        new(operation, organisation, 1, ClientExportContract.ParticipantId, LifecycleContractV1.Version,
            message, Guid.NewGuid(), correlation);

    private sealed class MemoryArtifactStore : IClientExportArtifactStore
    {
        private readonly ConcurrentDictionary<string, byte[]> content = new(StringComparer.Ordinal);

        public Task<string> PutIfAbsentAsync(Guid organisationId, Guid operationId, string category,
            string contentSha256, ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = $"memory://{organisationId:D}/{operationId:D}/{category}/{contentSha256}";
            var stored = content.GetOrAdd(reference, value.ToArray());
            if (!stored.AsSpan().SequenceEqual(value.Span))
                throw new InvalidOperationException("artifact_identity_conflict");
            return Task.FromResult(reference);
        }

        public byte[] Read(string reference) => content[reference];
    }
}

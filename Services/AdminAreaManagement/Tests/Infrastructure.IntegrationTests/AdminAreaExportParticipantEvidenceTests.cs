using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Exports;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Exports;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "Life01ProviderReal")]
public sealed class AdminAreaExportParticipantEvidenceTests(PostgreSqlRlsRuntimeDatabase database)
    : IClassFixture<PostgreSqlRlsRuntimeDatabase>, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"zeka-admin-export-{Guid.NewGuid():N}");

    [Fact]
    public async Task Fence_is_durable_idempotent_blocks_writes_and_stages_both_exact_fragments()
    {
        var operation = Guid.NewGuid();
        await using var provider = Provider(database.SeedOrganisation);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaExportParticipant>();
        var main = Header(operation, database.SeedOrganisation, AdminAreaExportContractV1.ParticipantId);

        var entered = await participant.EnterFenceAsync(new EnterOrganisationExportFenceV1(main));
        var replay = await participant.EnterFenceAsync(new EnterOrganisationExportFenceV1(main));
        Assert.Equal(entered, replay);

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var blocked = await Assert.ThrowsAnyAsync<Exception>(() => executor.ExecuteAsync(async token =>
        {
            context.Teams.Add(new Team("Blocked", "BLK"));
            await context.SaveChangesAsync(token);
            return true;
        }, default));
        Assert.Contains("export fence", blocked.ToString(), StringComparison.OrdinalIgnoreCase);

        var evidence = new CompleteExportFenceEvidenceV1(Guid.NewGuid(), new string('a', 64),
            [new ExportFenceParticipantRequirementV1(AdminAreaExportContractV1.ParticipantId, 1)],
            [new ExportFenceReceiptV1(entered)]);
        var snapshotAt = entered.EnteredAt.AddSeconds(1);
        var stage = new StageOrganisationExportV1(NewMessage(main), snapshotAt, evidence);
        var structured = await participant.StageFragmentAsync(stage);
        var structuredReplay = await participant.StageFragmentAsync(stage);
        Assert.Equal(structured.FragmentHash, structuredReplay.FragmentHash);
        Assert.Equal(AdminAreaExportContractV1.StructuredCategories,
            structured.Categories.Select(x => x.Category).ToArray());
        Assert.All(structured.Categories, category => Assert.NotEqual(
            ExportCategoryDispositionV1.NotImplemented, category.Disposition));
        var teamCount = await executor.ExecuteAsync(
            token => context.Teams.CountAsync(token), default);
        Assert.Equal(teamCount,
            structured.Categories.Single(x => x.Category == "teams").RecordCount);

        var documents = Header(operation, database.SeedOrganisation,
            AdminAreaExportContractV1.DocumentParticipantId);
        var documentFragment = await participant.StageFragmentAsync(new StageOrganisationExportV1(
            documents, snapshotAt, evidence, AdminAreaExportContractV1.ParticipantId));
        Assert.Equal("partner-document-artifacts", Assert.Single(documentFragment.Categories).Category);
        Assert.Equal(entered.FenceToken, documentFragment.FenceToken);

        var released = await participant.ReleaseFenceAsync(new ReleaseOrganisationExportFenceV1(
            NewMessage(main), entered.FenceToken));
        Assert.Equal(entered.FenceToken, released.FenceToken);

        await executor.ExecuteAsync(async token =>
        {
            var team = new Team("Allowed", "ALW");
            context.Teams.Add(team);
            await context.SaveChangesAsync(token);
            return true;
        }, default);
    }

    [Fact]
    public async Task Runtime_RLS_hides_other_organisation_participant_rows()
    {
        var other = Guid.NewGuid();
        await using var provider = Provider(database.SeedOrganisation);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaExportParticipant>();
        var header = Header(Guid.NewGuid(), database.SeedOrganisation, AdminAreaExportContractV1.ParticipantId);
        var entered = await participant.EnterFenceAsync(new EnterOrganisationExportFenceV1(header));

        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand(
            "select pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("organisation", other.ToString("D"));
            await set.ExecuteScalarAsync();
        }
        await using var count = new NpgsqlCommand(
            "select count(*) from public.\"AdminAreaExportFences\"", connection, transaction);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
        await transaction.RollbackAsync();
        await participant.ReleaseFenceAsync(new ReleaseOrganisationExportFenceV1(NewMessage(header), entered.FenceToken));
    }

    [Fact]
    public async Task Pending_or_failed_document_file_operation_blocks_fence_fail_closed()
    {
        var partner = await database.SeedPartnerAsAdministratorAsync(database.SeedOrganisation, "Fence blocker");
        var createOperation = Guid.NewGuid();
        await using var provider = Provider(database.SeedOrganisation);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        await executor.ExecuteAsync(async token =>
        {
            var document = new DocumentPartner
            {
                PartnerId = partner,
                Name = "pending.bin",
                Description = "pending fixture",
                ContentType = "application/octet-stream"
            };
            document.BeginFileWrite(createOperation, new string('b', 64), [1]);
            context.DocumentPartners.Add(document);
            await context.SaveChangesAsync(token);
            return true;
        }, default);

        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaExportParticipant>();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => participant.EnterFenceAsync(
            new EnterOrganisationExportFenceV1(Header(Guid.NewGuid(), database.SeedOrganisation,
                AdminAreaExportContractV1.ParticipantId))));
        Assert.Contains("document file operations", failure.Message, StringComparison.Ordinal);
        await database.ExecuteAdministratorAsync($"""
            DELETE FROM public."DocumentPartners" WHERE "CreateOperationId" = '{createOperation:D}'
            """);
    }

    private ServiceProvider Provider(Guid fixtureOrganisation)
    {
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["FileServerPath"] = Path.Combine(root, "documents")
        });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);
        services.AddAdminAreaFixtureExportParticipant(fixtureOrganisation, Path.Combine(root, "exports"));
        return services.BuildServiceProvider();
    }

    private static void Establish(AsyncServiceScope scope, Guid organisation) =>
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "life-01-adminarea-evidence"));

    private static LifecycleMessageHeaderV1 Header(Guid operation, Guid organisation, string participant) =>
        new(operation, organisation, 1, participant, 1, Guid.NewGuid(), Guid.NewGuid(), operation);

    private static LifecycleMessageHeaderV1 NewMessage(LifecycleMessageHeaderV1 header) =>
        new(header.OperationId, header.OrganisationId, header.OperationRevision, header.ParticipantId,
            header.ContractVersion, Guid.NewGuid(), header.MessageId, header.CorrelationId);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

[Trait("Issue", "46")]
[Trait("Evidence", "Life01Focused")]
public sealed class DeterministicFixtureExportArtifactStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"zeka-admin-artifacts-{Guid.NewGuid():N}");

    [Fact]
    public async Task Content_addressing_is_deterministic_and_missing_tampered_and_extra_files_fail_closed()
    {
        var store = new DeterministicFixtureExportArtifactStore(root);
        var organisation = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var write = new ExportArtifactWrite("admin-area", "teams", new byte[] { 1, 2, 3 });
        var first = await store.WriteVerifiedAsync(organisation, operation, write);
        var replay = await store.WriteVerifiedAsync(organisation, operation, write);
        Assert.Equal(first, replay);
        await store.VerifyExactAsync(organisation, operation, "admin-area", [first]);

        var path = Resolve(first.ArtifactReference);
        await File.WriteAllBytesAsync(path, [9]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyExactAsync(organisation, operation, "admin-area", [first]));
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "extra.artifact"), "extra");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyExactAsync(organisation, operation, "admin-area", [first]));
        File.Delete(Path.Combine(Path.GetDirectoryName(path)!, "extra.artifact"));
        File.Delete(path);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.VerifyExactAsync(organisation, operation, "admin-area", [first]));
    }

    [Fact]
    public void Symbolic_link_root_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = root + "-target";
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(root, target);
        try
        {
            Assert.Throws<InvalidDataException>(() => new DeterministicFixtureExportArtifactStore(root));
        }
        finally
        {
            Directory.Delete(root);
            Directory.Delete(target);
        }
    }

    private string Resolve(string reference) => Path.Combine(root,
        reference.Replace("admin-area-fixture/", string.Empty, StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "ApplicationConformance")]
public sealed class DocumentFileOperationEvidenceTests(PostgreSqlRlsRuntimeDatabase database)
    : IClassFixture<PostgreSqlRlsRuntimeDatabase>, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"zeka-document-operation-{Guid.NewGuid():N}");
    private const string RequestHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Failed_real_write_is_recorded_and_same_operation_reconciles_after_restart_without_duplicate()
    {
        var organisation = database.SeedOrganisation;
        var partnerId = await database.SeedPartnerAsAdministratorAsync(organisation, "Durable write");
        var operationId = Guid.NewGuid();
        byte[] content = [7, 8, 9];
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, organisation.ToString("N"));
        await File.WriteAllTextAsync(blocker, "blocks organisation directory creation");

        var failed = await PersistAsync(organisation, partnerId, operationId, content);

        Assert.Equal(DocumentFileOperationState.Failed, failed.FileWriteState);
        Assert.Equal("document_storage_unavailable", failed.FileWriteFailureCode);
        Assert.Equal(content, failed.PendingFileContent);
        Assert.Equal(1, failed.FileWriteAttempts);
        File.Delete(blocker);

        var recovered = await PersistAsync(organisation, partnerId, operationId, content);

        Assert.Equal(failed.Id, recovered.Id);
        Assert.Equal(DocumentFileOperationState.Completed, recovered.FileWriteState);
        Assert.Null(recovered.PendingFileContent);
        Assert.Equal(2, recovered.FileWriteAttempts);
        Assert.Equal(content, File.ReadAllBytes(CanonicalPath(organisation, partnerId, recovered.Id)));

        var duplicate = await PersistAsync(organisation, partnerId, operationId, content);
        Assert.Equal(recovered.Id, duplicate.Id);
        Assert.Equal(2, duplicate.FileWriteAttempts);
    }

    [Fact]
    public async Task Delete_is_durable_idempotent_and_retry_identity_cannot_target_another_document()
    {
        var organisation = database.SeedOrganisation;
        var partnerId = await database.SeedPartnerAsAdministratorAsync(organisation, "Durable delete");
        var first = await PersistAsync(organisation, partnerId, Guid.NewGuid(), [1, 2]);
        var second = await PersistAsync(organisation, partnerId, Guid.NewGuid(), [3, 4]);
        var deleteOperation = Guid.NewGuid();

        var deleted = await DeleteAsync(organisation, first.Id, deleteOperation);
        var duplicate = await DeleteAsync(organisation, first.Id, deleteOperation);

        Assert.Equal(DocumentFileOperationState.Completed, deleted.FileDeleteState);
        Assert.Equal(deleted.Id, duplicate.Id);
        Assert.Equal(1, duplicate.FileDeleteAttempts);
        Assert.False(File.Exists(CanonicalPath(organisation, partnerId, first.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeleteAsync(organisation, second.Id, deleteOperation));
    }

    [Fact]
    public async Task Same_create_operation_with_different_request_hash_conflicts_without_overwrite()
    {
        var organisation = database.SeedOrganisation;
        var partnerId = await database.SeedPartnerAsAdministratorAsync(organisation, "Durable conflict");
        var operation = Guid.NewGuid();
        var original = await PersistAsync(organisation, partnerId, operation, [4, 5]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PersistAsync(organisation, partnerId, operation, [9], new string('b', 64)));

        Assert.Equal([4, 5], File.ReadAllBytes(CanonicalPath(organisation, partnerId, original.Id)));
    }

    [Fact]
    public async Task Database_execution_strategy_replay_never_replays_the_filesystem_effect()
    {
        var organisation = database.SeedOrganisation;
        var partnerId = await database.SeedPartnerAsAdministratorAsync(organisation, "Replay boundary");
        var files = new CountingFileService(new AdminAreaManagement.Application.Common.Services.FileService(root));
        await using var provider = Provider(files);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, organisation);
        var repository = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var invocation = 0;
        var operationId = Guid.NewGuid();

        var result = await executor.ExecuteAsync(_ =>
        {
            invocation++;
            var document = repository.DocumentPartner.Persist(new DocumentPartner
            {
                PartnerId = partnerId,
                Name = "replay.bin",
                Description = "execution strategy boundary",
                ContentType = "application/octet-stream",
                ContentFile = [6, 7]
            }, operationId, RequestHash);
            if (invocation == 1)
                throw new NpgsqlException("Synthetic transient database failure.", new TimeoutException());
            return Task.FromResult(document);
        }, CancellationToken.None);

        Assert.Equal(2, invocation);
        Assert.Equal(1, files.SaveCalls);
        Assert.Equal(DocumentFileOperationState.Completed, result.FileWriteState);
    }

    private async Task<DocumentPartner> PersistAsync(Guid organisation, int partnerId, Guid operationId,
        byte[] content, string requestHash = RequestHash)
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, organisation);
        var repository = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        return await executor.ExecuteAsync(_ => Task.FromResult(repository.DocumentPartner.Persist(
            new DocumentPartner
            {
                PartnerId = partnerId,
                Name = "evidence.bin",
                Description = "durable operation evidence",
                ContentType = "application/octet-stream",
                ContentFile = content
            }, operationId, requestHash)), CancellationToken.None);
    }

    private async Task<DocumentPartner> DeleteAsync(Guid organisation, int documentId, Guid operationId)
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, organisation);
        var repository = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        return await executor.ExecuteAsync(_ =>
            Task.FromResult(repository.DocumentPartner.Delete(documentId, operationId)), CancellationToken.None);
    }

    private ServiceProvider Provider(IFileService? fileService = null)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["FileServerPath"] = root
        });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        if (fileService is not null)
        {
            services.RemoveAll<IFileService>();
            services.AddSingleton(fileService);
        }
        return services.BuildServiceProvider();
    }

    private static void Establish(AsyncServiceScope scope, Guid organisation) =>
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "document-evidence"));

    private string CanonicalPath(Guid organisation, int partnerId, int documentId) =>
        Path.Combine(root, organisation.ToString("N"), partnerId.ToString("D10"),
            $"document-{documentId:D10}.bin");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class CountingFileService(IFileService inner) : IFileService
    {
        public int SaveCalls { get; private set; }
        public byte[] GetContentFile(Guid organisationId, int partnerId, int docId) =>
            inner.GetContentFile(organisationId, partnerId, docId);
        public string GetFolderPath(Guid organisationId, int partnerId) =>
            inner.GetFolderPath(organisationId, partnerId);
        public void DeleteFile(Guid organisationId, int id, int partnerId) =>
            inner.DeleteFile(organisationId, id, partnerId);
        public void SaveFile(Guid organisationId, int id, int partnerId, byte[] contentFile)
        {
            SaveCalls++;
            inner.SaveFile(organisationId, id, partnerId, contentFile);
        }
    }
}

using System.Text.Json;
using System.Security.Cryptography;
using AdminAreaManagement.Application.Closures;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Common.Behaviours;
using AdminAreaManagement.Application.Teams.Commands.UpsertTeam;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Messaging;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "Life02ProviderReal")]
public sealed class AdminAreaClosureParticipantEvidenceTests(PostgreSqlRlsRuntimeDatabase database)
    : IClassFixture<PostgreSqlRlsRuntimeDatabase>
{
    [Fact]
    public async Task Both_boundaries_are_durable_idempotent_isolated_and_releasable_without_data_loss()
    {
        var operation = Guid.NewGuid();
        var other = Guid.NewGuid();
        await database.SeedTeamAsAdministratorAsync(other, "Other");
        var before = await CountAsync(database.SeedOrganisation, "Teams");
        var organisationBefore = await SnapshotAsync(database.SeedOrganisation, OrganisationTables);
        var otherBefore = await SnapshotAsync(other, OrganisationTables);
        var globalBefore = await SnapshotAsync(null, GlobalReferenceTables);

        await using var provider = Provider(database.SeedOrganisation);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var closingAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var admin = Header(operation, database.SeedOrganisation, AdminAreaClosureContractV1.ParticipantId);
        var documents = Header(operation, database.SeedOrganisation, AdminAreaClosureContractV1.DocumentParticipantId);

        var adminReceipt = await participant.EnterFenceAsync(new(admin, closingAt));
        Assert.NotEqual(admin.MessageId, adminReceipt.Header.MessageId);
        Assert.Equal(admin.MessageId, adminReceipt.Header.CausationId);
        Assert.Equal(adminReceipt.Header.MessageId,
            await ReadOutboxHeaderMessageIdAsync(adminReceipt.Header.MessageId));
        Assert.Equal(adminReceipt, await participant.EnterFenceAsync(new(admin, closingAt)));
        var documentReceipt = await participant.EnterFenceAsync(new(documents, closingAt));
        Assert.Equal(AdminAreaClosureContractV1.DocumentParticipantId, documentReceipt.Header.ParticipantId);

        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteRuntimeAsync(database.SeedOrganisation,
                "UPDATE public.\"Teams\" SET \"Name\"=\"Name\" WHERE \"OrganisationId\"=@organisation"));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, denied.SqlState);

        Assert.Equal(before, await CountAsync(database.SeedOrganisation, "Teams"));
        Assert.Equal(organisationBefore, await SnapshotAsync(database.SeedOrganisation, OrganisationTables));
        Assert.Equal(otherBefore, await SnapshotAsync(other, OrganisationTables));
        Assert.Equal(globalBefore, await SnapshotAsync(null, GlobalReferenceTables));

        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.ReleaseFenceAsync(
            new(RecoveryMessageAt(documents, documentReceipt.Header, documents.OperationRevision),
                documentReceipt.FenceToken)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.ReleaseFenceAsync(
            new(RecoveryMessageAt(documents, documentReceipt.Header, documents.OperationRevision + 2),
                documentReceipt.FenceToken)));
        var acceptedRecovery = RecoveryMessage(documents, documentReceipt.Header);
        var wrongCausation = new LifecycleMessageHeaderV1(
            acceptedRecovery.OperationId, acceptedRecovery.OrganisationId, acceptedRecovery.OperationRevision,
            acceptedRecovery.ParticipantId, acceptedRecovery.ContractVersion, acceptedRecovery.MessageId,
            documents.MessageId, acceptedRecovery.CorrelationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.ReleaseFenceAsync(
            new(wrongCausation, documentReceipt.FenceToken)));
        var rawRelease = await Assert.ThrowsAsync<PostgresException>(() => ExecuteRuntimeAsync(
            database.SeedOrganisation,
            $$"""
                UPDATE public."AdminAreaClosureFences" SET "ReleasedAt"=CURRENT_TIMESTAMP
                WHERE "OperationId"='{{documents.OperationId:D}}'::uuid
                  AND "ParticipantId"='{{documents.ParticipantId}}'
                """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rawRelease.SqlState);
        await AssertOwnerReleaseDeniedAsync(acceptedRecovery, documentReceipt.FenceToken + "-wrong");
        await AssertRuntimeClosureDeniedAsync(database.SeedOrganisation,
            "UPDATE public.\"Teams\" SET \"Name\"=\"Name\" WHERE \"OrganisationId\"=@organisation");
        var releaseDocuments = new ReleaseOrganisationClosureFenceV1(
            RecoveryMessage(documents, documentReceipt.Header), documentReceipt.FenceToken);
        var releasedDocuments = await participant.ReleaseFenceAsync(releaseDocuments);
        Assert.Equal(documentReceipt.FenceToken, releasedDocuments.FenceToken);
        Assert.NotEqual(releaseDocuments.Header.MessageId, releasedDocuments.Header.MessageId);
        Assert.Equal(releaseDocuments.Header.MessageId, releasedDocuments.Header.CausationId);
        Assert.Equal(releasedDocuments.Header.MessageId,
            await ReadOutboxHeaderMessageIdAsync(releasedDocuments.Header.MessageId));
        Assert.Equal(releasedDocuments, await participant.ReleaseFenceAsync(releaseDocuments));
        await participant.ReleaseFenceAsync(new(RecoveryMessage(admin, adminReceipt.Header), adminReceipt.FenceToken));

        var allowed = await ExecuteRuntimeAsync(database.SeedOrganisation,
            "UPDATE public.\"Teams\" SET \"Name\"=\"Name\" WHERE \"OrganisationId\"=@organisation");
        Assert.True(allowed > 0);
        Assert.Equal(before, await CountAsync(database.SeedOrganisation, "Teams"));
        Assert.Equal(organisationBefore, await SnapshotAsync(database.SeedOrganisation, OrganisationTables));
        Assert.Equal(otherBefore, await SnapshotAsync(other, OrganisationTables));
        Assert.Equal(globalBefore, await SnapshotAsync(null, GlobalReferenceTables));
    }

    [Fact]
    public async Task Skewed_closing_time_is_the_normalized_replay_stable_boundary()
    {
        var now = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero).AddTicks(9);
        var closingAt = now.AddMinutes(5).AddTicks(7);
        var clock = new MutableTimeProvider(now);
        await using var provider = Provider(database.SeedOrganisation, clock: clock);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var header = Header(Guid.NewGuid(), database.SeedOrganisation, AdminAreaClosureContractV1.ParticipantId);

        var receipt = await participant.EnterFenceAsync(new(header, closingAt));
        Assert.Equal(LifecycleContractTimeV1.Normalize(closingAt), receipt.BoundaryEstablishedAt);
        Assert.Equal(receipt, await participant.EnterFenceAsync(new(header, closingAt)));

        var release = new ReleaseOrganisationClosureFenceV1(
            RecoveryMessage(header, receipt.Header), receipt.FenceToken);
        var released = await participant.ReleaseFenceAsync(release);
        Assert.True(released.ReleasedAt >= receipt.BoundaryEstablishedAt);
        Assert.Equal(receipt.BoundaryEstablishedAt, released.ReleasedAt);
        Assert.Equal(released, await participant.ReleaseFenceAsync(release));
    }

    [Theory]
    [InlineData(AdminAreaClosureContractV1.ParticipantId)]
    [InlineData(AdminAreaClosureContractV1.DocumentParticipantId)]
    public async Task Noncanonical_command_ids_fail_before_persistence_and_canonical_commands_replay(
        string participantId)
    {
        await using var provider = Provider(database.SeedOrganisation);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var operation = Guid.NewGuid();
        var header = Header(operation, database.SeedOrganisation, participantId);
        var closingAt = DateTimeOffset.UtcNow;
        var noncanonicalEnter = WithMessageId(header, Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            participant.EnterFenceAsync(new(noncanonicalEnter, closingAt)));
        Assert.Equal((0L, 0L, 0L), await ClosureRecordCountsAsync(operation));

        var wrongCausation = new LifecycleMessageHeaderV1(
            header.OperationId, header.OrganisationId, header.OperationRevision, header.ParticipantId,
            header.ContractVersion, header.MessageId, Guid.NewGuid(), header.CorrelationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            participant.EnterFenceAsync(new(wrongCausation, closingAt)));
        Assert.Equal((0L, 0L, 0L), await ClosureRecordCountsAsync(operation));

        var wrongCorrelation = new LifecycleMessageHeaderV1(
            header.OperationId, header.OrganisationId, header.OperationRevision, header.ParticipantId,
            header.ContractVersion, header.MessageId, header.CausationId, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            participant.EnterFenceAsync(new(wrongCorrelation, closingAt)));
        Assert.Equal((0L, 0L, 0L), await ClosureRecordCountsAsync(operation));

        var enter = new CloseOrganisationParticipantV1(header, closingAt);
        var receipt = await participant.EnterFenceAsync(enter);
        Assert.Equal(receipt, await participant.EnterFenceAsync(enter));
        var releaseHeader = RecoveryMessage(header, receipt.Header);
        var noncanonicalRelease = WithMessageId(releaseHeader, Guid.NewGuid());
        var beforeRejectedRelease = await ClosureRecordCountAsync(operation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.ReleaseFenceAsync(
            new(noncanonicalRelease, receipt.FenceToken)));
        Assert.Equal(beforeRejectedRelease, await ClosureRecordCountAsync(operation));

        var release = new ReleaseOrganisationClosureFenceV1(releaseHeader, receipt.FenceToken);
        var released = await participant.ReleaseFenceAsync(release);
        Assert.Equal(released, await participant.ReleaseFenceAsync(release));
    }

    [Fact]
    public async Task Pre_boundary_transaction_finishes_before_fence_and_application_command_is_denied_afterward()
    {
        var operation = Guid.NewGuid();
        await using var provider = Provider(database.SeedOrganisation);
        await using var ordinaryScope = provider.CreateAsyncScope();
        Establish(ordinaryScope, database.SeedOrganisation);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinary = ordinaryScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>()
            .ExecuteAsync(async token =>
            {
                await ordinaryScope.ServiceProvider.GetRequiredService<IAdminAreaClosureGate>()
                    .DemandOrdinaryAccessAsync(token);
                started.TrySetResult();
                await proceed.Task.WaitAsync(token);
                ordinaryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .Add(new Team("Admitted before closure", Guid.NewGuid().ToString("N")[..6]));
                await ordinaryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(token);
                return true;
            }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var closureScope = provider.CreateAsyncScope();
        Establish(closureScope, database.SeedOrganisation);
        var participant = closureScope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var header = Header(operation, database.SeedOrganisation, AdminAreaClosureContractV1.ParticipantId);
        var closure = participant.EnterFenceAsync(new(header, DateTimeOffset.UtcNow));
        await Task.Delay(100);
        Assert.False(closure.IsCompleted);
        proceed.TrySetResult();
        await ordinary.WaitAsync(TimeSpan.FromSeconds(5));
        var receipt = await closure.WaitAsync(TimeSpan.FromSeconds(5));

        await using var deniedScope = provider.CreateAsyncScope();
        var tenant = deniedScope.ServiceProvider.GetRequiredService<TenantContextScope>();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            new(database.SeedOrganisation, Guid.NewGuid(), ["TeamConfiguration.ManageTeams"],
                "life02", DateTimeOffset.UtcNow)));
        var operationContext = new TenantOperation(access,
            new Identity("life02-admin-command", database.SeedOrganisation), tenant, tenant);
        var behaviour = new AuthorizationBehaviour<UpsertTeamCommand, int>(operationContext,
            deniedScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>(),
            deniedScope.ServiceProvider.GetRequiredService<IAdminAreaClosureGate>());
        var called = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => behaviour.Handle(
            new() { Name = "Denied", Acronym = "DEN" }, () =>
            {
                called = true;
                return Task.FromResult(1);
            }, default));
        Assert.False(called);
        await participant.ReleaseFenceAsync(new(RecoveryMessage(header, receipt.Header), receipt.FenceToken));
    }

    [Fact]
    public async Task Admin_first_boundary_refuses_until_admitted_write_drains_then_fences_without_mutating_content()
    {
        var partner = await database.SeedPartnerAsAdministratorAsync(database.SeedOrganisation, "Closure drain");
        var root = Path.Combine(Path.GetTempPath(), $"zeka-life02-document-drain-{Guid.NewGuid():N}");
        var files = new BlockingFileService(
            new AdminAreaManagement.Application.Common.Services.FileService(root));
        await using var provider = Provider(database.SeedOrganisation, files);
        var content = new byte[] { 11, 22, 33, 44 };
        var operation = Guid.NewGuid();
        var pendingDocument = new DocumentPartner
        {
            PartnerId = partner,
            Name = "drain.bin",
            Description = "closure drain evidence",
            ContentType = "application/octet-stream",
            ContentFile = content
        };

        await using var writeScope = provider.CreateAsyncScope();
        Establish(writeScope, database.SeedOrganisation);
        var repository = writeScope.ServiceProvider.GetRequiredService<IRepositoryManager>();
        var executor = writeScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var write = Task.Run(() => executor.ExecuteAsync(_ => Task.FromResult(
            repository.DocumentPartner.Persist(pendingDocument, operation, new string('d', 64))),
            CancellationToken.None));
        await files.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using var closureScope = provider.CreateAsyncScope();
        Establish(closureScope, database.SeedOrganisation);
        var participant = closureScope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var adminHeader = Header(Guid.NewGuid(), database.SeedOrganisation,
            AdminAreaClosureContractV1.ParticipantId);
        var header = Header(Guid.NewGuid(), database.SeedOrganisation,
            AdminAreaClosureContractV1.DocumentParticipantId);
        var adminCommand = new CloseOrganisationParticipantV1(adminHeader, DateTimeOffset.UtcNow);
        var documentCommand = new CloseOrganisationParticipantV1(header, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.EnterFenceAsync(adminCommand));
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.EnterFenceAsync(documentCommand));
        Assert.Equal(0L, await ClosureRecordCountAsync(adminHeader.OperationId));
        Assert.Equal(0L, await ClosureRecordCountAsync(header.OperationId));

        files.Release.TrySetResult();
        var document = await write.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DocumentFileOperationState.Completed, document.FileWriteState);
        var adminReceipt = await participant.EnterFenceAsync(adminCommand);
        Assert.Equal(adminReceipt, await participant.EnterFenceAsync(adminCommand));
        var receipt = await participant.EnterFenceAsync(documentCommand);
        Assert.Equal(receipt, await participant.EnterFenceAsync(documentCommand));
        Assert.Equal(content, files.GetContentFile(database.SeedOrganisation, partner, document.Id));
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(document.ContentFile));
        var filesAtBoundary = FileSnapshot(root);
        Assert.Equal(1, filesAtBoundary.Count);

        await using var deniedScope = provider.CreateAsyncScope();
        Establish(deniedScope, database.SeedOrganisation);
        var deniedRepository = deniedScope.ServiceProvider.GetRequiredService<IRepositoryManager>();
        var deniedExecutor = deniedScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var denied = await Assert.ThrowsAnyAsync<Exception>(() => deniedExecutor.ExecuteAsync(_ => Task.FromResult(
            deniedRepository.DocumentPartner.Persist(new DocumentPartner
            {
                PartnerId = partner,
                Name = "denied.bin",
                Description = "must not start",
                ContentType = "application/octet-stream",
                ContentFile = [99]
            }, Guid.NewGuid(), new string('e', 64))), CancellationToken.None));
        var postgres = ExceptionChain(denied).OfType<PostgresException>().Single();
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, postgres.SqlState);
        Assert.Equal(1, files.SaveCalls);
        Assert.Equal(filesAtBoundary, FileSnapshot(root));
        await AssertRuntimeClosureDeniedAsync(database.SeedOrganisation, $$"""
            SELECT pg_catalog.set_config('zeka.adminarea_document_recovery','on',true);
            UPDATE public."DocumentPartners" SET "FileWriteState"=1 WHERE "Id"={{document.Id}}
            """);

        await participant.ReleaseFenceAsync(new(RecoveryMessage(header, receipt.Header), receipt.FenceToken));
        await participant.ReleaseFenceAsync(new(RecoveryMessage(adminHeader, adminReceipt.Header),
            adminReceipt.FenceToken));
        Assert.Equal(content, files.GetContentFile(database.SeedOrganisation, partner, document.Id));
        Assert.Equal(filesAtBoundary, FileSnapshot(root));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Document_first_boundary_refuses_until_admitted_delete_drains_then_fences()
    {
        var partner = await database.SeedPartnerAsAdministratorAsync(database.SeedOrganisation, "Closure delete drain");
        var root = Path.Combine(Path.GetTempPath(), $"zeka-life02-document-delete-{Guid.NewGuid():N}");
        var inner = new AdminAreaManagement.Application.Common.Services.FileService(root);
        var files = new BlockingDeleteFileService(inner);
        await using var provider = Provider(database.SeedOrganisation, files);

        async Task<DocumentPartner> CreateAsync(string name, byte[] content)
        {
            await using var scope = provider.CreateAsyncScope();
            Establish(scope, database.SeedOrganisation);
            var repository = scope.ServiceProvider.GetRequiredService<IRepositoryManager>();
            return await scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>()
                .ExecuteAsync(_ => Task.FromResult(repository.DocumentPartner.Persist(new DocumentPartner
                {
                    PartnerId = partner,
                    Name = name,
                    Description = "delete drain evidence",
                    ContentType = "application/octet-stream",
                    ContentFile = content
                }, Guid.NewGuid(), new string('c', 64))), CancellationToken.None);
        }

        var target = await CreateAsync("delete.bin", [1, 3, 5, 7]);
        var retained = await CreateAsync("retained.bin", [2, 4, 6, 8]);
        var retainedBytes = inner.GetContentFile(database.SeedOrganisation, partner, retained.Id);

        await using var deleteScope = provider.CreateAsyncScope();
        Establish(deleteScope, database.SeedOrganisation);
        var deleteRepository = deleteScope.ServiceProvider.GetRequiredService<IRepositoryManager>();
        var delete = Task.Run(() => deleteScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>()
            .ExecuteAsync(_ => Task.FromResult(deleteRepository.DocumentPartner.Delete(target.Id, Guid.NewGuid())),
                CancellationToken.None));
        await files.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var immutableMetadata = await DocumentInvariantAsync(target.Id);

        await using var closureScope = provider.CreateAsyncScope();
        Establish(closureScope, database.SeedOrganisation);
        var participant = closureScope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var documents = Header(Guid.NewGuid(), database.SeedOrganisation,
            AdminAreaClosureContractV1.DocumentParticipantId);
        var closeDocuments = new CloseOrganisationParticipantV1(documents, DateTimeOffset.UtcNow);
        var admin = Header(Guid.NewGuid(), database.SeedOrganisation, AdminAreaClosureContractV1.ParticipantId);
        var closeAdmin = new CloseOrganisationParticipantV1(admin, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.EnterFenceAsync(closeDocuments));
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.EnterFenceAsync(closeAdmin));
        Assert.Equal(0L, await ClosureRecordCountAsync(documents.OperationId));
        Assert.Equal(0L, await ClosureRecordCountAsync(admin.OperationId));

        files.DeleteRelease.TrySetResult();
        var deleted = await delete.WaitAsync(TimeSpan.FromSeconds(10));
        await using var restartScope = provider.CreateAsyncScope();
        Establish(restartScope, database.SeedOrganisation);
        var restartParticipant = restartScope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var documentReceipt = await restartParticipant.EnterFenceAsync(closeDocuments);
        Assert.Equal(documentReceipt, await restartParticipant.EnterFenceAsync(closeDocuments));
        var adminReceipt = await restartParticipant.EnterFenceAsync(closeAdmin);
        Assert.Equal(adminReceipt, await restartParticipant.EnterFenceAsync(closeAdmin));
        Assert.Equal(DocumentFileOperationState.Completed, deleted.FileDeleteState);
        Assert.Throws<FileNotFoundException>(() => inner.GetContentFile(
            database.SeedOrganisation, partner, target.Id));
        Assert.Equal(retainedBytes, inner.GetContentFile(database.SeedOrganisation, partner, retained.Id));
        Assert.Equal(immutableMetadata, await DocumentInvariantAsync(target.Id));

        await using var deniedScope = provider.CreateAsyncScope();
        Establish(deniedScope, database.SeedOrganisation);
        var deniedRepository = deniedScope.ServiceProvider.GetRequiredService<IRepositoryManager>();
        var deniedExecutor = deniedScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        await Assert.ThrowsAsync<ApplicationException>(() => deniedExecutor.ExecuteAsync(_ => Task.FromResult(
            deniedRepository.DocumentPartner.Delete(retained.Id, Guid.NewGuid())), CancellationToken.None));
        Assert.Equal(1, files.DeleteCalls);
        Assert.Equal(retainedBytes, inner.GetContentFile(database.SeedOrganisation, partner, retained.Id));

        await restartParticipant.ReleaseFenceAsync(new(RecoveryMessage(documents, documentReceipt.Header),
            documentReceipt.FenceToken));
        await restartParticipant.ReleaseFenceAsync(new(RecoveryMessage(admin, adminReceipt.Header), adminReceipt.FenceToken));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Projection_publication_rechecks_the_gate_before_the_external_effect()
    {
        var publisher = new Publisher();
        await using var provider = Provider(database.SeedOrganisation);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var databaseContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var message = new StaffProjectionChangedV1(database.SeedOrganisation, Guid.NewGuid(), 1,
            true, "Closure", "Evidence", "closure-evidence", "Evidence", "EVD");
        var row = await executor.ExecuteAsync(async token =>
        {
            var row = new StaffProjectionMessage { EventId = message.Id, Payload = JsonSerializer.Serialize(message) };
            row.AssignToOrganisation(database.SeedOrganisation);
            databaseContext.Add(row);
            await databaseContext.SaveChangesAsync(token);
            return row;
        }, default);

        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var header = Header(Guid.NewGuid(), database.SeedOrganisation, AdminAreaClosureContractV1.ParticipantId);
        var entered = await participant.EnterFenceAsync(new(header, DateTimeOffset.UtcNow));

        var outbox = new StaffProjectionOutbox(databaseContext,
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>(), publisher,
            NullLogger<StaffProjectionOutbox>.Instance, Configuration(database.SeedOrganisation),
            scope.ServiceProvider.GetRequiredService<IAdminAreaClosureGate>());
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            token => outbox.DispatchAsync(token), default));
        Assert.Contains("closure boundary", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(0, publisher.Calls);
        await participant.ReleaseFenceAsync(new(RecoveryMessage(header, entered.Header),
            (await ReadFenceAsync(database.SeedOrganisation, header.ParticipantId)).FenceToken));
        await executor.ExecuteAsync(async token =>
        {
            databaseContext.Remove(row);
            await databaseContext.SaveChangesAsync(token);
            return true;
        }, default);
    }

    [Fact]
    public async Task Pre_boundary_projection_publication_commits_before_fence_and_database_denies_later_DML()
    {
        var publisher = new BlockingPublisher();
        await using var provider = Provider(database.SeedOrganisation);
        await using var dispatchScope = provider.CreateAsyncScope();
        Establish(dispatchScope, database.SeedOrganisation);
        var databaseContext = dispatchScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var executor = dispatchScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var message = new StaffProjectionChangedV1(database.SeedOrganisation, Guid.NewGuid(), 2,
            true, "Pre", "Boundary", "pre-boundary", "Evidence", "PBE");
        var row = await executor.ExecuteAsync(async token =>
        {
            var staged = new StaffProjectionMessage
                { EventId = message.Id, Payload = JsonSerializer.Serialize(message) };
            staged.AssignToOrganisation(database.SeedOrganisation);
            databaseContext.Add(staged);
            await databaseContext.SaveChangesAsync(token);
            return staged;
        }, default);

        var outbox = new StaffProjectionOutbox(databaseContext,
            dispatchScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>(), publisher,
            NullLogger<StaffProjectionOutbox>.Instance, Configuration(database.SeedOrganisation),
            dispatchScope.ServiceProvider.GetRequiredService<IAdminAreaClosureGate>());
        var dispatch = executor.ExecuteAsync(token => outbox.DispatchAsync(token), default);
        await publisher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var closureScope = provider.CreateAsyncScope();
        Establish(closureScope, database.SeedOrganisation);
        var participant = closureScope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var header = Header(Guid.NewGuid(), database.SeedOrganisation, AdminAreaClosureContractV1.ParticipantId);
        var closure = participant.EnterFenceAsync(new(header, DateTimeOffset.UtcNow));
        await Task.Delay(100);
        Assert.False(closure.IsCompleted);

        publisher.Release.TrySetResult();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        var receipt = await closure.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, publisher.Calls);
        Assert.NotNull(row.PublishedAtUtc);

        await AssertRuntimeClosureDeniedAsync(database.SeedOrganisation,
            $"UPDATE public.\"StaffProjectionOutbox\" SET \"PublishedAtUtc\"=NULL WHERE \"Id\"={row.Id}");
        await AssertRuntimeClosureDeniedAsync(database.SeedOrganisation,
            $"DELETE FROM public.\"StaffProjectionOutbox\" WHERE \"Id\"={row.Id}");
        await AssertRuntimeClosureDeniedAsync(database.SeedOrganisation, $$"""
            INSERT INTO public."StaffProjectionOutbox"
              ("EventId","Payload","Created","CreatedBy","LastModifiedBy","Softdelete","OrganisationId")
            VALUES ('{{Guid.NewGuid():D}}'::uuid,'{}',CURRENT_TIMESTAMP,'evidence','evidence',false,@organisation)
            """);

        await participant.ReleaseFenceAsync(new(RecoveryMessage(header, receipt.Header), receipt.FenceToken));
    }

    [Fact]
    public async Task Cross_identity_and_conflicting_replays_fail_closed_and_RLS_hides_evidence()
    {
        var operation = Guid.NewGuid();
        await using var provider = Provider(database.SeedOrganisation);
        await using var scope = provider.CreateAsyncScope();
        Establish(scope, database.SeedOrganisation);
        var participant = scope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>();
        var header = Header(operation, database.SeedOrganisation, AdminAreaClosureContractV1.ParticipantId);
        var entered = await participant.EnterFenceAsync(new(header, DateTimeOffset.UtcNow.AddSeconds(-2)));

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => participant.EnterFenceAsync(
            new(header, DateTimeOffset.UtcNow.AddSeconds(-1))));
        Assert.Contains("reused", conflict.Message, StringComparison.OrdinalIgnoreCase);

        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var other = Guid.NewGuid();
        await using (var tenant = new NpgsqlCommand(
            "select pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            tenant.Parameters.AddWithValue("organisation", other.ToString("D"));
            await tenant.ExecuteScalarAsync();
        }
        await using var count = new NpgsqlCommand(
            "select count(*) from public.\"AdminAreaClosureFences\"", connection, transaction);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
        await transaction.RollbackAsync();
        await participant.ReleaseFenceAsync(new(RecoveryMessage(header, entered.Header), entered.FenceToken));
    }

    private ServiceProvider Provider(Guid fixtureOrganisation, IFileService? files = null,
        TimeProvider? clock = null)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["FileServerPath"] = Path.Combine(Path.GetTempPath(), "zeka-life02-admin-files"),
            ["TenantWorker:OrganisationIds:0"] = fixtureOrganisation.ToString()
        });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);
        if (clock is not null)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(clock);
        }
        if (files is not null)
        {
            services.RemoveAll<IFileService>();
            services.AddSingleton(files);
        }
        services.AddAdminAreaFixtureClosureParticipants(fixtureOrganisation);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(Guid organisation) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        { ["TenantWorker:OrganisationIds:0"] = organisation.ToString() }).Build();

    private static void Establish(AsyncServiceScope scope, Guid organisation) =>
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "life-02-adminarea-evidence"));

    private static LifecycleMessageHeaderV1 Header(Guid operation, Guid organisation, string participant) =>
        new(operation, organisation, 2, participant, LifecycleContractV1.Version,
            LifecycleMessageIdentityV1.ForPhase(operation, participant, "enter-fence", 2),
            operation, operation);

    private static LifecycleMessageHeaderV1 RecoveryMessage(
        LifecycleMessageHeaderV1 header, LifecycleMessageHeaderV1 acceptedEnterReceipt) =>
        RecoveryMessageAt(header, acceptedEnterReceipt, checked(header.OperationRevision + 1));

    private static LifecycleMessageHeaderV1 RecoveryMessageAt(
        LifecycleMessageHeaderV1 header, LifecycleMessageHeaderV1 acceptedEnterReceipt, long revision) =>
        new(header.OperationId, header.OrganisationId, revision, header.ParticipantId,
            header.ContractVersion,
            LifecycleMessageIdentityV1.ForPhase(header.OperationId, header.ParticipantId, "release-fence",
                revision),
            acceptedEnterReceipt.MessageId, header.CorrelationId);

    private static LifecycleMessageHeaderV1 WithMessageId(
        LifecycleMessageHeaderV1 header, Guid messageId) =>
        new(header.OperationId, header.OrganisationId, header.OperationRevision, header.ParticipantId,
            header.ContractVersion, messageId, header.CausationId, header.CorrelationId);

    private sealed class BlockingFileService(IFileService inner) : IFileService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCalls { get; private set; }

        public void SaveFile(Guid organisationId, int id, int partnerId, byte[] contentFile)
        {
            SaveCalls++;
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            inner.SaveFile(organisationId, id, partnerId, contentFile);
        }

        public byte[] GetContentFile(Guid organisationId, int partnerId, int docId) =>
            inner.GetContentFile(organisationId, partnerId, docId);
        public string GetFolderPath(Guid organisationId, int partnerId) =>
            inner.GetFolderPath(organisationId, partnerId);
        public void DeleteFile(Guid organisationId, int id, int partnerId) =>
            inner.DeleteFile(organisationId, id, partnerId);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }

    private sealed class BlockingDeleteFileService(IFileService inner) : IFileService
    {
        public TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DeleteCalls { get; private set; }

        public void SaveFile(Guid organisationId, int id, int partnerId, byte[] contentFile) =>
            inner.SaveFile(organisationId, id, partnerId, contentFile);
        public byte[] GetContentFile(Guid organisationId, int partnerId, int docId) =>
            inner.GetContentFile(organisationId, partnerId, docId);
        public string GetFolderPath(Guid organisationId, int partnerId) =>
            inner.GetFolderPath(organisationId, partnerId);
        public void DeleteFile(Guid organisationId, int id, int partnerId)
        {
            DeleteCalls++;
            DeleteStarted.TrySetResult();
            DeleteRelease.Task.GetAwaiter().GetResult();
            inner.DeleteFile(organisationId, id, partnerId);
        }
    }

    private async Task<int> ExecuteRuntimeAsync(Guid organisation, string sql)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenant = new NpgsqlCommand(
            "select pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            tenant.Parameters.AddWithValue("organisation", organisation.ToString("D"));
            await tenant.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("organisation", organisation);
        var result = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return result;
    }

    private async Task AssertRuntimeClosureDeniedAsync(Guid organisation, string sql)
    {
        var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteRuntimeAsync(organisation, sql));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, denied.SqlState);
    }

    private async Task AssertOwnerReleaseDeniedAsync(LifecycleMessageHeaderV1 header, string fenceToken)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenant = new NpgsqlCommand(
            "select pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            tenant.Parameters.AddWithValue("organisation", header.OrganisationId.ToString("D"));
            await tenant.ExecuteScalarAsync();
        }
        await using var release = new NpgsqlCommand("""
            SELECT zeka.release_adminarea_closure_fence(
              @operation,@organisation,@participant,@revision,@token,@contract,
              @message,@causation,@correlation,@released_at)
            """, connection, transaction);
        release.Parameters.AddWithValue("operation", header.OperationId);
        release.Parameters.AddWithValue("organisation", header.OrganisationId);
        release.Parameters.AddWithValue("participant", header.ParticipantId);
        release.Parameters.AddWithValue("revision", header.OperationRevision);
        release.Parameters.AddWithValue("token", fenceToken);
        release.Parameters.AddWithValue("contract", header.ContractVersion);
        release.Parameters.AddWithValue("message", header.MessageId);
        release.Parameters.AddWithValue("causation", header.CausationId);
        release.Parameters.AddWithValue("correlation", header.CorrelationId);
        release.Parameters.AddWithValue("released_at", DateTimeOffset.UtcNow);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => release.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, denied.SqlState);
        await transaction.RollbackAsync();
    }

    private async Task<long> CountAsync(Guid organisation, string table)
    {
        if (table != "Teams") throw new ArgumentOutOfRangeException(nameof(table));
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenant = new NpgsqlCommand(
            "select pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            tenant.Parameters.AddWithValue("organisation", organisation.ToString("D"));
            await tenant.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand("select count(*) from public.\"Teams\"", connection, transaction);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> DocumentInvariantAsync(int documentId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (pg_catalog.to_jsonb(d)
              - ARRAY['FileWriteState','FileWriteAttempts','FileWriteFailureCode','PendingFileContent',
                      'FileDeleteState','FileDeleteAttempts','FileDeleteFailureCode']::text[])::text
            FROM public."DocumentPartners" d WHERE d."Id"=@document
            """, connection);
        command.Parameters.AddWithValue("document", documentId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task<long> ClosureRecordCountAsync(Guid operationId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM public."AdminAreaClosureFences" WHERE "OperationId"=@operation)
              + (SELECT count(*) FROM public."AdminAreaClosureInbox" WHERE "OperationId"=@operation)
              + (SELECT count(*) FROM public."AdminAreaClosureOutbox" WHERE "OperationId"=@operation)
            """, connection);
        command.Parameters.AddWithValue("operation", operationId);
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private async Task<(long Fences, long Inbox, long Outbox)> ClosureRecordCountsAsync(Guid operationId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM public."AdminAreaClosureFences" WHERE "OperationId"=@operation),
              (SELECT count(*) FROM public."AdminAreaClosureInbox" WHERE "OperationId"=@operation),
              (SELECT count(*) FROM public."AdminAreaClosureOutbox" WHERE "OperationId"=@operation)
            """, connection);
        command.Parameters.AddWithValue("operation", operationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static readonly string[] OrganisationTables =
        ["Teams", "StaffMembers", "Partners", "ContactPersons", "Emails", "DocumentPartners", "StaffProjectionOutbox"];
    private static readonly string[] GlobalReferenceTables =
        ["Cities", "Nationalities", "Professions", "Schools", "Trainings", "TrainingFields", "TrainingTypes"];

    private async Task<string> SnapshotAsync(Guid? organisation, IEnumerable<string> tables)
    {
        var allowed = OrganisationTables.Concat(GlobalReferenceTables).ToHashSet(StringComparer.Ordinal);
        var identities = new List<string>();
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            if (!allowed.Contains(table)) throw new ArgumentOutOfRangeException(nameof(tables));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var where = organisation is null ? string.Empty : " WHERE \"OrganisationId\"=@organisation";
            await using var command = new NpgsqlCommand(
                $"SELECT value FROM (SELECT row_to_json(source)::text AS value FROM public.\"{table}\" source{where}) ordered ORDER BY value",
                connection);
            if (organisation is not null) command.Parameters.AddWithValue("organisation", organisation.Value);
            await using var reader = await command.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(reader.GetString(0));
                hash.AppendData(BitConverter.GetBytes(bytes.Length));
                hash.AppendData(bytes);
                count++;
            }
            identities.Add($"{table}:{count}:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}");
        }
        return string.Join('\n', identities);
    }

    private static (int Count, string Sha256) FileSnapshot(string root)
    {
        var files = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ToArray()
            : [];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var name = System.Text.Encoding.UTF8.GetBytes(relative);
            var bytes = File.ReadAllBytes(file);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return (files.Length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            yield return current;
    }

    private async Task<(string FenceToken, DateTimeOffset EnteredAt)> ReadFenceAsync(
        Guid organisation, string participantId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select "FenceToken", "EnteredAt" from public."AdminAreaClosureFences"
            where "OrganisationId"=@organisation and "ParticipantId"=@participant and "ReleasedAt" is null
            """, connection);
        command.Parameters.AddWithValue("organisation", organisation);
        command.Parameters.AddWithValue("participant", participantId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task<Guid> ReadOutboxHeaderMessageIdAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            select "PayloadJson"::text from public."AdminAreaClosureOutbox"
            where "MessageId"=@message
            """, connection);
        command.Parameters.AddWithValue("message", messageId);
        var payload = Assert.IsType<string>(await command.ExecuteScalarAsync());
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("header").GetProperty("messageId").GetGuid();
    }

    private sealed class Publisher : IEventBus
    {
        public int Calls { get; private set; }
        public Task PublishAsync(Event message) { Calls++; return Task.CompletedTask; }
    }

    private sealed class BlockingPublisher : IEventBus
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task PublishAsync(Event message)
        {
            Calls++;
            Started.TrySetResult();
            return Release.Task;
        }
    }

    private sealed record Identity(string SubjectId, Guid SelectedOrganisationId) : IOperationIdentity;
    private sealed class Access(TenantAccessDecision decision) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId,
            Guid selectedOrganisationId, CancellationToken cancellationToken) => Task.FromResult(decision);
    }
}

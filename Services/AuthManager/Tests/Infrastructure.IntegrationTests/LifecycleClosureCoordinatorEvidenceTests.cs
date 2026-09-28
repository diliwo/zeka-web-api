using AuthManager.Application.Authorization;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Enums;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Lifecycle;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Persistence.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.Json;
using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "46")]
[Trait("Evidence", "LIFE-02")]
public sealed class LifecycleClosureCoordinatorEvidenceTests(PostgreSqlFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string MigratorPassword = "synthetic-life02-migrator";
    private const string RuntimePassword = "synthetic-life02-runtime";
    private const string RecoveryPassword = "synthetic-life02-recovery";
    private static readonly Guid OrganisationA = Guid.Parse("46020000-0000-0000-0000-000000000001");
    private static readonly Guid OrganisationB = Guid.Parse("46020000-0000-0000-0000-000000000099");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Provider_real_closure_is_restart_safe_fences_only_A_and_archives_after_all_receipts()
    {
        var environment = await CreateEnvironmentAsync();
        var registry = ReviewedClosureRegistryV1.Create();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation)
                .ActivateAsync(registry, 0, default));
        }

        var clock = new FixedClock(Now.AddMinutes(1).AddTicks(9));
        var admission = await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options(environment.RuntimeConnection), clock))
            .AdmitAsync(environment.OwnerUserId, OrganisationA,
                LifecycleOperationFamily.Termination, Guid.NewGuid());
        var operation = Assert.IsType<LifecycleOperation>(admission.Operation);
        Assert.Equal(registry.InventoryHash, operation.InventoryHash);
        Assert.Equal(11, operation.Participants.Count);

        var coordinator = new LifecycleClosureCoordinator(
            new LifecycleClosureStore(Options(environment.RuntimeConnection), clock));
        var globalBefore = await GlobalReferenceDigest(environment.AdministratorConnection);
        var organisationBBefore = await OrganisationDigest(
            environment.AdministratorConnection, OrganisationB);
        var retainedBefore = await TenantOwnedCounts(
            environment.AdministratorConnection, OrganisationA);
        Assert.Equal(ClosureProgressStatus.Progressed,
            (await coordinator.BeginAsync(operation.Id, OrganisationA)).Status);

        Assert.Null(await RuntimeMembershipUpdate(environment.RuntimeConnection, OrganisationA,
            environment.OwnerMembershipA));

        // A new coordinator and participant instance proves recovery from durable state only.
        coordinator = new LifecycleClosureCoordinator(
            new LifecycleClosureStore(Options(environment.RuntimeConnection), clock));
        var recovered = Assert.Single(await coordinator.RecoverAsync(OrganisationA));
        Assert.Equal(operation.Id, recovered.OperationId);
        Assert.Equal(ClosureProgressStatus.AwaitingParticipants, recovered.Status);
        var auth = Participant(environment, clock);
        var authCommand = (await ReadCloseCommands(environment.RuntimeConnection, OrganisationA))
            .First(x => x.Header.ParticipantId == "auth-management");
        await using var beforeBoundaryConnection = new NpgsqlConnection(environment.RuntimeConnection);
        await beforeBoundaryConnection.OpenAsync();
        await using var beforeBoundary = await beforeBoundaryConnection.BeginTransactionAsync();
        await SetContext(beforeBoundaryConnection, beforeBoundary, OrganisationA);
        await using (var mutation = new NpgsqlCommand("""
            UPDATE public."OrganisationMemberships"
            SET "ConcurrencyVersion"="ConcurrencyVersion"+1 WHERE "Id"=@id
            """, beforeBoundaryConnection, beforeBoundary))
        {
            mutation.Parameters.AddWithValue("id", environment.OwnerMembershipA);
            Assert.Equal(1, await mutation.ExecuteNonQueryAsync());
        }
        var firstEnter = auth.EnterAsync(authCommand);
        var duplicateEnter = Participant(environment, clock)
            .EnterAsync(authCommand);
        await Task.Delay(100);
        Assert.False(firstEnter.IsCompleted);
        Assert.False(duplicateEnter.IsCompleted);
        await beforeBoundary.CommitAsync();
        var concurrent = await Task.WhenAll(firstEnter, duplicateEnter);
        Assert.Equal(concurrent[0].ReceiptHash, concurrent[1].ReceiptHash);
        Assert.Equal(0, concurrent[0].BoundaryEstablishedAt.Ticks % 10);
        Assert.Equal(ClosureProgressStatus.AwaitingParticipants,
            (await coordinator.ReceiveAsync(concurrent[0])).Status);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            await RuntimeMembershipUpdate(environment.RuntimeConnection, OrganisationA,
                environment.OwnerMembershipA));

        var delayedAdminHeader = ParticipantReplyHeader(operation.Id, "admin-area");
        Assert.Equal(ClosureProgressStatus.Conflict,
            (await coordinator.ReceiveAsync(new OrganisationClosureParticipantCompletedV1(
                delayedAdminHeader, "admin-area-closure-token", 1, Now))).Status);
        foreach (var participant in new[] { "admin-area", "admin-area-documents", "client-management" })
        {
            var boundaryAt = participant == "admin-area-documents"
                ? Now.AddMinutes(10) : Now.AddMinutes(1);
            var completed = new OrganisationClosureParticipantCompletedV1(
                participant == "admin-area" ? delayedAdminHeader
                    : ParticipantReplyHeader(operation.Id, participant),
                $"{participant}-closure-token", 1,
                boundaryAt);
            var result = await coordinator.ReceiveAsync(completed);
            Assert.Contains(result.Status,
                [ClosureProgressStatus.AwaitingParticipants, ClosureProgressStatus.Archived]);
        }

        Assert.Equal($"{(int)OrganisationStatus.Archived}|{(int)LifecycleOperationState.Completed}|false|4",
            await RuntimeScalar(environment.RuntimeConnection, OrganisationA, """
                SELECT o."Status"::text || '|' || op."State"::text || '|' || op."IsActive"::text
                       || '|' || (SELECT count(*) FROM public."LifecycleClosureFenceReceipts"
                                  WHERE "OperationId"=op."Id")::text
                FROM public."Organisations" o
                JOIN public."OrganisationLifecycleOperations" op ON op."OrganisationId"=o."Id"
                WHERE o."Id"=current_setting('zeka.organisation_id')::uuid
                """));
        Assert.Equal(((int)OrganisationStatus.Active).ToString(),
            await RuntimeScalar(environment.RuntimeConnection, OrganisationB,
                "SELECT \"Status\"::text FROM public.\"Organisations\" WHERE \"Id\"=current_setting('zeka.organisation_id')::uuid"));
        await using (var accessDatabase = new AuthDbContext(Options(environment.RuntimeConnection)))
            Assert.Equal(TenantAccessOutcome.Denied,
                (await new CurrentTenantAccessResolver(accessDatabase, clock)
                    .ResolveAsync(environment.OwnerUserId, OrganisationA)).Outcome);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            await RuntimeMembershipUpdate(environment.RuntimeConnection, OrganisationA,
                environment.OwnerMembershipA));
        var archivedValue = await RuntimeScalar(environment.RuntimeConnection, OrganisationA,
            "SELECT \"ArchivedAt\" FROM public.\"OrganisationLifecycleOperations\" LIMIT 1");
        var archivedAt = archivedValue is DateTimeOffset offset ? offset
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)archivedValue!, DateTimeKind.Utc));
        Assert.Equal(Now.AddMinutes(10), archivedAt);
        var postArchiveRelease = new ReleaseOrganisationClosureFenceV1(
            Header(operation.Id, "auth-management", concurrent[0].Header.OperationRevision + 1,
                causationId: concurrent[0].Header.MessageId), concurrent[0].FenceToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Participant(environment, clock)
                .ReleaseAsync(postArchiveRelease));
        Assert.Equal(((int)AuthClosureParticipantState.FenceEntered).ToString(),
            await RuntimeScalar(environment.RuntimeConnection, OrganisationA,
                "SELECT \"State\"::text FROM public.\"AuthClosureParticipantExecutions\" LIMIT 1"));
        Assert.Equal(globalBefore,
            await GlobalReferenceDigest(environment.AdministratorConnection));
        Assert.Equal(organisationBBefore,
            await OrganisationDigest(environment.AdministratorConnection, OrganisationB));
        var retainedAfter = await TenantOwnedCounts(
            environment.AdministratorConnection, OrganisationA);
        Assert.All(retainedBefore, row =>
            Assert.True(retainedAfter[row.Key] >= row.Value,
                $"Auth-owned rows were lost from {row.Key}."));
        var archivedPublisher = new BlockingPublisher();
        await using var archivedProvider = DispatcherProvider(
            environment.RuntimeConnection, clock, archivedPublisher);
        await using var archivedScope = archivedProvider.CreateAsyncScope();
        Assert.Equal(10, await archivedScope.ServiceProvider
            .GetRequiredService<IOutboxDispatcher>().DispatchBatchAsync());
        Assert.Contains(archivedPublisher.Messages,
            x => x.MessageType == nameof(OrganisationArchivedV1));
    }

    [Fact]
    public async Task Provider_real_pre_archive_recovery_releases_only_established_fences()
    {
        var environment = await CreateEnvironmentAsync();
        var registry = ReviewedClosureRegistryV1.Create();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation)
                .ActivateAsync(registry, 0, default));
        }
        var globalBefore = await GlobalReferenceDigest(environment.AdministratorConnection);
        var organisationBBefore = await OrganisationDigest(
            environment.AdministratorConnection, OrganisationB);
        var clock = new FixedClock(Now.AddMinutes(1).AddTicks(9));
        var operation = Assert.IsType<LifecycleOperation>((await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options(environment.RuntimeConnection), clock))
            .AdmitAsync(environment.OwnerUserId, OrganisationA,
                LifecycleOperationFamily.Termination, Guid.NewGuid())).Operation);
        var coordinator = new LifecycleClosureCoordinator(
            new LifecycleClosureStore(Options(environment.RuntimeConnection), clock));
        await coordinator.BeginAsync(operation.Id, OrganisationA);
        var auth = Participant(environment, clock);
        var authCommand = (await ReadCloseCommands(environment.RuntimeConnection, OrganisationA))
            .First(x => x.Header.ParticipantId == "auth-management");
        var randomHeader = new LifecycleMessageHeaderV1(authCommand.Header.OperationId,
            authCommand.Header.OrganisationId, authCommand.Header.OperationRevision,
            authCommand.Header.ParticipantId, authCommand.Header.ContractVersion, Guid.NewGuid(),
            authCommand.Header.CausationId, authCommand.Header.CorrelationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.EnterAsync(
            new CloseOrganisationParticipantV1(randomHeader, authCommand.ClosingAt)));
        Assert.Equal("0|0|0", await RuntimeScalar(environment.RuntimeConnection, OrganisationA, """
            SELECT (SELECT count(*) FROM public."AuthClosureParticipantExecutions")::text || '|'
              || (SELECT count(*) FROM public."AuthClosureParticipantInbox")::text || '|'
              || (SELECT count(*) FROM public."AuthClosureParticipantOutbox")::text
            """));
        var entered = await auth.EnterAsync(authCommand);
        await coordinator.ReceiveAsync(new OrganisationClosureParticipantFailedV1(
            ParticipantReplyHeader(operation.Id, "auth-management", "enter-failed"),
            OrganisationClosureParticipantPhaseV1.EnterFence,
            "lost-ack-ambiguous", false, Now.AddMinutes(1)));
        await coordinator.ReceiveAsync(new OrganisationClosureParticipantFailedV1(
            ParticipantReplyHeader(operation.Id, "client-management", "enter-failed"),
            OrganisationClosureParticipantPhaseV1.EnterFence,
            "synthetic-nonretryable", false, Now.AddMinutes(1),
            OrganisationClosureBoundaryDispositionV1.NotEstablished));
        Assert.Equal(ClosureProgressStatus.AwaitingParticipants,
            (await coordinator.RecoverBeforeArchiveAsync(operation.Id, OrganisationA)).Status);
        foreach (var participant in new[] { "admin-area", "admin-area-documents" })
            await coordinator.ReceiveAsync(new OrganisationClosureParticipantFailedV1(
                ParticipantReplyHeader(operation.Id, participant, "enter-failed"),
                OrganisationClosureParticipantPhaseV1.EnterFence,
                "synthetic-unavailable", true, Now.AddMinutes(1),
                OrganisationClosureBoundaryDispositionV1.NotEstablished));

        Assert.Equal(ClosureProgressStatus.AwaitingParticipants,
            Assert.Single(await coordinator.RecoverAsync(OrganisationA)).Status);
        var retried = await ReadCloseCommands(environment.RuntimeConnection, OrganisationA);
        Assert.Single(retried.Where(x => x.Header.ParticipantId == "client-management"));
        Assert.Equal(2, retried.Count(x => x.Header.ParticipantId == "admin-area"));
        Assert.Equal(2, retried.Count(x => x.Header.ParticipantId == "admin-area-documents"));
        Assert.Equal($"{(int)LifecycleParticipantState.Failed}|false|synthetic-nonretryable",
            await RuntimeScalar(environment.RuntimeConnection, OrganisationA, $"""
                SELECT "State"::text || '|' || "FailureRetryable"::text || '|' || "FailureCode"
                FROM public."OrganisationLifecycleParticipants"
                WHERE "OperationId"='{operation.Id:D}'::uuid
                  AND "ParticipantId"='client-management' AND "Family"=1
                """));

        Assert.Equal(ClosureProgressStatus.Conflict,
            (await coordinator.RecoverBeforeArchiveAsync(operation.Id, OrganisationA)).Status);
        Assert.Equal(ClosureProgressStatus.AwaitingParticipants,
            (await coordinator.ReceiveAsync(entered)).Status);
        Assert.Equal(ClosureProgressStatus.RecoveryStarted,
            (await coordinator.RecoverBeforeArchiveAsync(operation.Id, OrganisationA)).Status);
        coordinator = new LifecycleClosureCoordinator(
            new LifecycleClosureStore(Options(environment.RuntimeConnection), clock));
        Assert.Equal(ClosureProgressStatus.RecoveryStarted,
            Assert.Single(await coordinator.RecoverAsync(OrganisationA)).Status);
        var releaseCommands = await ReadReleaseCommands(
            environment.RuntimeConnection, OrganisationA);
        Assert.Equal(2, releaseCommands.Count);
        Assert.Single(releaseCommands.Select(x => x.Header.MessageId).Distinct());
        Assert.All(releaseCommands, command =>
        {
            Assert.Equal(3, command.Header.OperationRevision);
            Assert.Equal(entered.Header.MessageId, command.Header.CausationId);
            Assert.Equal(operation.Id, command.Header.CorrelationId);
        });
        var recoveryPublisher = new BlockingPublisher();
        await using var recoveryProvider = DispatcherProvider(
            environment.RuntimeConnection, clock, recoveryPublisher);
        await using var recoveryScope = recoveryProvider.CreateAsyncScope();
        Assert.Equal(9, await recoveryScope.ServiceProvider
            .GetRequiredService<IOutboxDispatcher>().DispatchBatchAsync());
        Assert.Contains(recoveryPublisher.Messages,
            x => x.MessageType == nameof(ReleaseOrganisationClosureFenceV1));
        var authAfterRestart = Participant(environment, clock);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            await RuntimeCommandError(environment.RuntimeConnection, OrganisationA, $"""
                UPDATE public."AuthClosureParticipantExecutions"
                SET "State"=2, "ReleasedAt"=now()
                WHERE "OperationId"='{operation.Id:D}'::uuid
                """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            await RuntimeCommandError(environment.RuntimeConnection, OrganisationA, $"""
                SELECT zeka.release_auth_closure_fence(
                  '{operation.Id:D}'::uuid, '{OrganisationA:D}'::uuid, 3,
                  'wrong-token', '{entered.Header.MessageId:D}'::uuid,
                  '{operation.Id:D}'::uuid, now())
                """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            await RuntimeCommandError(environment.RuntimeConnection, OrganisationA, $"""
                SELECT zeka.release_auth_closure_fence(
                  '{operation.Id:D}'::uuid, '{OrganisationA:D}'::uuid, 3,
                  '{entered.FenceToken}', '{entered.Header.MessageId:D}'::uuid,
                  '{operation.Id:D}'::uuid, NULL)
                """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            await RuntimeCommandError(environment.RuntimeConnection, OrganisationA, $"""
                SELECT zeka.release_auth_closure_fence(
                  '{operation.Id:D}'::uuid, '{OrganisationA:D}'::uuid, 3,
                  '{entered.FenceToken}', '{entered.Header.MessageId:D}'::uuid,
                  '{operation.Id:D}'::uuid,
                  '{entered.BoundaryEstablishedAt.AddMilliseconds(-1):O}'::timestamptz)
                """));
        Assert.Equal(((int)AuthClosureParticipantState.FenceEntered).ToString(),
            await RuntimeScalar(environment.RuntimeConnection, OrganisationA,
                "SELECT \"State\"::text FROM public.\"AuthClosureParticipantExecutions\" LIMIT 1"));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            await RuntimeMembershipUpdate(environment.RuntimeConnection, OrganisationA,
                environment.OwnerMembershipA));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authAfterRestart.ReleaseAsync(
            new ReleaseOrganisationClosureFenceV1(
                Header(operation.Id, "auth-management", 2,
                    messageId: LifecycleMessageIdentityV1.ForPhase(
                        operation.Id, "auth-management", "release-fence", 2),
                    causationId: entered.Header.MessageId), entered.FenceToken)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authAfterRestart.ReleaseAsync(
            new ReleaseOrganisationClosureFenceV1(
                Header(operation.Id, "auth-management", 4,
                    messageId: LifecycleMessageIdentityV1.ForPhase(
                        operation.Id, "auth-management", "release-fence", 4),
                    causationId: entered.Header.MessageId), entered.FenceToken)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authAfterRestart.ReleaseAsync(
            new ReleaseOrganisationClosureFenceV1(
                Header(operation.Id, "auth-management", 3,
                    messageId: LifecycleMessageIdentityV1.ForPhase(
                        operation.Id, "auth-management", "release-fence", 3),
                    causationId: entered.Header.MessageId), "wrong-auth-fence-token")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authAfterRestart.ReleaseAsync(
            new ReleaseOrganisationClosureFenceV1(
                Header(operation.Id, "auth-management", 3,
                    causationId: entered.Header.MessageId), entered.FenceToken)));
        var releaseCommand = new ReleaseOrganisationClosureFenceV1(
            Header(operation.Id, "auth-management", 3,
                messageId: LifecycleMessageIdentityV1.ForPhase(
                    operation.Id, "auth-management", "release-fence", 3),
                causationId: entered.Header.MessageId), entered.FenceToken);
        var recoveryCapability = new NpgsqlAuthClosureRecoveryCapability(
            environment.RecoveryConnection);
        await recoveryCapability.ReleaseAsync(releaseCommand,
            LifecycleContractTimeV1.Normalize(clock.GetUtcNow()));
        await recoveryCapability.ReleaseAsync(releaseCommand,
            LifecycleContractTimeV1.Normalize(clock.GetUtcNow()));
        var released = await authAfterRestart.ReleaseAsync(releaseCommand);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            await RuntimeMembershipUpdate(environment.RuntimeConnection, OrganisationA,
                environment.OwnerMembershipA));
        Assert.Equal(ClosureProgressStatus.Recovered,
            (await coordinator.ReceiveAsync(released)).Status);
        Assert.Equal(released.ReceiptHash,
            (await Participant(environment, clock)
                .ReleaseAsync(releaseCommand)).ReceiptHash);
        Assert.Null(await RuntimeMembershipUpdate(environment.RuntimeConnection, OrganisationA,
            environment.OwnerMembershipA));
        Assert.Equal($"{(int)OrganisationStatus.Active}|{(int)LifecycleOperationState.Failed}|false|ClosureRecoveredBeforeArchive",
            await RuntimeScalar(environment.RuntimeConnection, OrganisationA, """
                SELECT o."Status"::text || '|' || op."State"::text || '|' || op."IsActive"::text
                       || '|' || op."FailureCode"
                FROM public."Organisations" o
                JOIN public."OrganisationLifecycleOperations" op ON op."OrganisationId"=o."Id"
                WHERE o."Id"=current_setting('zeka.organisation_id')::uuid
                """));
        Assert.Equal(globalBefore,
            await GlobalReferenceDigest(environment.AdministratorConnection));
        Assert.Equal(organisationBBefore,
            await OrganisationDigest(environment.AdministratorConnection, OrganisationB));
    }

    [Fact]
    public async Task Fixture_dispatch_replays_stable_outbox_identity_and_archives_only_after_all_four_boundaries()
    {
        var environment = await CreateEnvironmentAsync();
        var registry = ReviewedClosureRegistryV1.Create();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation)
                .ActivateAsync(registry, 0, default));
        }
        var clock = new FixedClock(Now.AddMinutes(1));
        var operation = Assert.IsType<LifecycleOperation>((await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options(environment.RuntimeConnection), clock))
            .AdmitAsync(environment.OwnerUserId, OrganisationA,
                LifecycleOperationFamily.Termination, Guid.NewGuid())).Operation);
        var coordinator = new LifecycleClosureCoordinator(
            new LifecycleClosureStore(Options(environment.RuntimeConnection), clock));

        Assert.Equal(ClosureProgressStatus.Progressed,
            (await coordinator.BeginAsync(operation.Id, OrganisationA)).Status);
        var first = await ReadCloseCommands(environment.RuntimeConnection, OrganisationA);
        Assert.Equal(4, first.Count);
        Assert.Equal(4, first.Select(x => x.Header.MessageId).Distinct().Count());
        Assert.Equal("5|4", await RuntimeScalar(environment.RuntimeConnection, OrganisationA, $"""
            SELECT (SELECT count(*) FROM public."OutboxMessages"
                    WHERE "CorrelationId"='{operation.Id:D}')::text
                   || '|' || (SELECT count(*) FROM public."OrganisationLifecycleParticipants"
                               WHERE "OperationId"='{operation.Id:D}'::uuid
                                 AND "Family"=1 AND "State"=1)::text
            """));

        // A restart re-emits the same four command identities; no new protocol identity is invented.
        Assert.Equal(ClosureProgressStatus.AwaitingParticipants,
            Assert.Single(await coordinator.RecoverAsync(OrganisationA)).Status);
        var replayed = await ReadCloseCommands(environment.RuntimeConnection, OrganisationA);
        Assert.Equal(8, replayed.Count);
        Assert.Equal(4, replayed.Select(x => x.Header.MessageId).Distinct().Count());
        Assert.All(replayed.GroupBy(x => x.Header.ParticipantId), group =>
            Assert.Single(group.Select(x => x.Header.MessageId).Distinct()));

        var dispatcher = new FixtureClosureContractDispatcher(
            environment.RuntimeConnection, clock, coordinator);
        var authCommand = first.Single(x => x.Header.ParticipantId == "auth-management");
        var authParticipant = Participant(environment, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => authParticipant.EnterAsync(
            new CloseOrganisationParticipantV1(new LifecycleMessageHeaderV1(
                authCommand.Header.OperationId, authCommand.Header.OrganisationId,
                authCommand.Header.OperationRevision, authCommand.Header.ParticipantId,
                authCommand.Header.ContractVersion, authCommand.Header.MessageId,
                Guid.NewGuid(), authCommand.Header.CorrelationId), authCommand.ClosingAt)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => authParticipant.EnterAsync(
            new CloseOrganisationParticipantV1(new LifecycleMessageHeaderV1(
                authCommand.Header.OperationId, authCommand.Header.OrganisationId,
                authCommand.Header.OperationRevision, authCommand.Header.ParticipantId,
                authCommand.Header.ContractVersion, authCommand.Header.MessageId,
                authCommand.Header.CausationId, Guid.NewGuid()), authCommand.ClosingAt)));
        Assert.Equal("0|0|0", await RuntimeScalar(environment.RuntimeConnection, OrganisationA, $"""
            SELECT (SELECT count(*) FROM public."AuthClosureParticipantInbox")::text || '|'
                || (SELECT count(*) FROM public."AuthClosureParticipantExecutions")::text || '|'
                || (SELECT count(*) FROM public."AuthClosureParticipantOutbox")::text
            """));
        await dispatcher.DispatchAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Participant(environment, clock)
                .EnterAsync(new CloseOrganisationParticipantV1(
                    authCommand.Header, authCommand.ClosingAt.AddSeconds(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Participant(environment, clock)
                .EnterAsync(new CloseOrganisationParticipantV1(
                    new LifecycleMessageHeaderV1(authCommand.Header.OperationId,
                        authCommand.Header.OrganisationId, authCommand.Header.OperationRevision,
                        "client-management", authCommand.Header.ContractVersion, Guid.NewGuid(),
                        authCommand.Header.CausationId, authCommand.Header.CorrelationId),
                    authCommand.ClosingAt)));
        // A reconstructed fixture dispatcher represents a lost acknowledgement/restart and replays
        // byte-stable participant outputs into the coordinator's durable inbox.
        await new FixtureClosureContractDispatcher(environment.RuntimeConnection, clock, coordinator)
            .DispatchAsync(first);

        Assert.Equal($"{(int)OrganisationStatus.Archived}|{(int)LifecycleOperationState.Completed}|4",
            await RuntimeScalar(environment.RuntimeConnection, OrganisationA, """
                SELECT o."Status"::text || '|' || op."State"::text || '|'
                       || (SELECT count(*) FROM public."LifecycleClosureFenceReceipts"
                           WHERE "OperationId"=op."Id")::text
                FROM public."Organisations" o
                JOIN public."OrganisationLifecycleOperations" op ON op."OrganisationId"=o."Id"
                WHERE o."Id"=current_setting('zeka.organisation_id')::uuid
                """));
    }

    [Fact]
    public async Task Concurrent_termination_admission_has_one_durable_operation_and_one_replay()
    {
        var environment = await CreateEnvironmentAsync();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation)
                .ActivateAsync(ReviewedClosureRegistryV1.Create(), 0, default));
        }
        var idempotencyId = Guid.NewGuid();
        async Task<AdmissionResult> Admit() => await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options(environment.RuntimeConnection),
                new FixedClock(Now.AddMinutes(1))))
            .AdmitAsync(environment.OwnerUserId, OrganisationA,
                LifecycleOperationFamily.Termination, idempotencyId);
        var results = await Task.WhenAll(Admit(), Admit());
        Assert.Single(results.Where(x => x.Status == AdmissionStatus.Admitted));
        Assert.Single(results.Where(x => x.Status == AdmissionStatus.Replay));
        Assert.Single(results.Select(x => x.Operation!.Id).Distinct());
        Assert.Equal("1|4", await RuntimeScalar(environment.RuntimeConnection, OrganisationA, """
            SELECT count(DISTINCT op."Id")::text || '|' || count(p."ParticipantId")::text
            FROM public."OrganisationLifecycleOperations" op
            JOIN public."OrganisationLifecycleParticipants" p ON p."OperationId"=op."Id"
            WHERE op."Family"=1 AND p."Family"=1
              AND p."CapabilityKey"='organisation.closure-fence'
            """));
    }

    [Fact]
    public async Task Provider_real_outbox_publication_respects_the_Auth_closure_boundary()
    {
        var environment = await CreateEnvironmentAsync();
        var registry = ReviewedClosureRegistryV1.Create();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation)
                .ActivateAsync(registry, 0, default));
        }
        var clock = new FixedClock(Now.AddMinutes(1).AddTicks(9));
        var operation = Assert.IsType<LifecycleOperation>((await new LifecycleAdmission(
            new CurrentAccess(), new LifecycleAdmissionStore(
                Options(environment.RuntimeConnection), clock))
            .AdmitAsync(environment.OwnerUserId, OrganisationA,
                LifecycleOperationFamily.Termination, Guid.NewGuid())).Operation);
        var coordinator = new LifecycleClosureCoordinator(
            new LifecycleClosureStore(Options(environment.RuntimeConnection), clock));
        await coordinator.BeginAsync(operation.Id, OrganisationA);

        var publisher = new BlockingPublisher();
        await using var provider = DispatcherProvider(environment.RuntimeConnection, clock, publisher);
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        Assert.Equal(5, await dispatcher.DispatchBatchAsync());
        Assert.All(publisher.Messages, message =>
            Assert.Contains(message.MessageType,
                [nameof(CloseOrganisationParticipantV1), nameof(OrganisationClosingV1)]));

        var firstOrdinaryId = await StageOutbox(environment.RuntimeConnection,
            "SyntheticOrdinaryOrganisationMessageV1", "{}", operation.Id, clock.GetUtcNow());
        publisher.BlockNext();
        var dispatchBeforeBoundary = dispatcher.DispatchBatchAsync();
        await publisher.WaitUntilBlockedAsync();
        var auth = Participant(environment, clock);
        var enter = auth.EnterAsync((await ReadCloseCommands(
            environment.RuntimeConnection, OrganisationA))
            .First(x => x.Header.ParticipantId == "auth-management"));
        await Task.Delay(100);
        Assert.False(enter.IsCompleted);
        publisher.Release();
        Assert.Equal(1, await dispatchBeforeBoundary);
        var entered = await enter;
        Assert.Contains(publisher.Messages, x => x.Id == firstOrdinaryId);

        var blockedOrdinaryId = await StageOutbox(environment.RuntimeConnection,
            "SyntheticOrdinaryOrganisationMessageV1", "{}", operation.Id, clock.GetUtcNow());
        var publishedCount = publisher.Messages.Count;
        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
        Assert.Equal(publishedCount, publisher.Messages.Count);
        Assert.Equal("|OrganisationLifecycleBoundary", await ExecuteScalar(
            environment.AdministratorConnection, $"""
                SELECT coalesce("ProcessedAtUtc"::text, '') || '|' || coalesce("LastError", '')
                FROM public."OutboxMessages" WHERE "Id"='{blockedOrdinaryId:D}'::uuid
                """));

        var forgedHeader = new LifecycleMessageHeaderV1(operation.Id, OrganisationA,
            entered.Header.OperationRevision, "auth-management", LifecycleContractV1.Version,
            Guid.NewGuid(), operation.Id, operation.Id);
        var forged = new CloseOrganisationParticipantV1(forgedHeader,
            LifecycleContractTimeV1.Normalize(clock.GetUtcNow()));
        var forgedId = await StageOutbox(environment.RuntimeConnection,
            nameof(CloseOrganisationParticipantV1), JsonSerializer.Serialize(forged, Json),
            operation.Id, clock.GetUtcNow());
        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
        Assert.DoesNotContain(publisher.Messages, x => x.Id == forgedId);
        Assert.Equal("|OrganisationLifecycleBoundary", await ExecuteScalar(
            environment.AdministratorConnection, $"""
                SELECT coalesce("ProcessedAtUtc"::text, '') || '|' || coalesce("LastError", '')
                FROM public."OutboxMessages" WHERE "Id"='{forgedId:D}'::uuid
                """));

        var factHeader = new LifecycleMessageHeaderV1(operation.Id, OrganisationA,
            entered.Header.OperationRevision, "auth-management", LifecycleContractV1.Version,
            LifecycleMessageIdentityV1.ForPhase(operation.Id, "auth-management",
                "closing-fact", entered.Header.OperationRevision),
            operation.Id, operation.Id);
        var fact = new OrganisationClosingV1(factHeader,
            LifecycleContractTimeV1.Normalize(clock.GetUtcNow()), registry.Revision,
            registry.InventoryHash);
        var lifecycleId = await StageOutbox(environment.RuntimeConnection,
            nameof(OrganisationClosingV1), JsonSerializer.Serialize(fact, Json), operation.Id,
            clock.GetUtcNow());
        Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        Assert.Contains(publisher.Messages, x => x.Id == lifecycleId);

        Assert.Equal(ClosureProgressStatus.AwaitingParticipants,
            (await coordinator.ReceiveAsync(entered)).Status);
        foreach (var participantId in new[] { "admin-area", "admin-area-documents", "client-management" })
        {
            var header = ParticipantReplyHeader(operation.Id, participantId);
            var result = await coordinator.ReceiveAsync(new OrganisationClosureParticipantCompletedV1(
                header, $"{participantId}-closure-token", 1,
                LifecycleContractTimeV1.Normalize(clock.GetUtcNow())));
            Assert.Contains(result.Status,
                [ClosureProgressStatus.AwaitingParticipants, ClosureProgressStatus.Archived]);
        }

        var archivedOrdinaryId = await StageOutbox(environment.RuntimeConnection,
            "SyntheticArchivedOrdinaryOrganisationMessageV1", "{}", operation.Id, clock.GetUtcNow());
        var publishedAfterArchive = publisher.Messages.Count;
        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
        Assert.Equal(publishedAfterArchive, publisher.Messages.Count);
        Assert.Equal("|OrganisationArchived|0|{}|", await ExecuteScalar(
            environment.AdministratorConnection, $"""
                SELECT coalesce("ProcessedAtUtc"::text, '') || '|' || coalesce("LastError", '')
                       || '|' || "AttemptCount"::text || '|' || "Payload"
                       || '|' || coalesce("LeaseId"::text, '')
                FROM public."OutboxMessages" WHERE "Id"='{archivedOrdinaryId:D}'::uuid
                """));

        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
        Assert.Equal(publishedAfterArchive, publisher.Messages.Count);
        Assert.Equal(1L, await ExecuteScalar(environment.AdministratorConnection, $"""
            SELECT count(*) FROM public."OutboxMessages"
            WHERE "Id"='{archivedOrdinaryId:D}'::uuid
              AND "ProcessedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NOT NULL
            """));
    }

    private async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var administrator = await fixture.CreateDatabaseAsync();
        await Execute(administrator, Bootstrap());
        await Execute(administrator,
            $"ALTER ROLE zeka_auth_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_auth_runtime PASSWORD '{RuntimePassword}';");
        var migrator = Connection(administrator, "zeka_auth_migrator", MigratorPassword);
        var runtime = Connection(administrator, "zeka_auth_runtime", RuntimePassword);
        await using (var database = new AuthDbContext(Options(migrator)))
        {
            await database.Database.OpenConnectionAsync();
            await database.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await database.GetService<IMigrator>().MigrateAsync();
        }
        await Execute(administrator, Bootstrap());
        await Execute(administrator, $"""
            DO $block$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                             WHERE rolname='zeka_auth_closure_recovery_test') THEN
                CREATE ROLE zeka_auth_closure_recovery_test LOGIN
                  PASSWORD '{RecoveryPassword}';
              END IF;
            END $block$;
            ALTER ROLE zeka_auth_closure_recovery_test PASSWORD '{RecoveryPassword}';
            GRANT zeka_auth_closure_recovery TO zeka_auth_closure_recovery_test;
            """);

        var owner = User.Create("life02-owner@example.invalid", "life02-owner", "Synthetic", "Owner", Now);
        owner.EmailConfirmed = true;
        var organisationA = Organisation.Create(OrganisationA, "Synthetic LIFE-02 A", owner.Id, Now);
        var organisationB = Organisation.Create(OrganisationB, "Synthetic LIFE-02 B", owner.Id, Now);
        Assert.True(organisationA.Activate(Now));
        Assert.True(organisationB.Activate(Now));
        var membershipA = OrganisationMembership.CreateOwner(Guid.NewGuid(), OrganisationA, owner.Id, Now);
        var membershipB = OrganisationMembership.CreateOwner(Guid.NewGuid(), OrganisationB, owner.Id, Now);
        var organisationBGrant = MembershipPermissionGrant.Create(Guid.NewGuid(), OrganisationB,
            membershipB.Id, "synthetic.organisation-b.permission", membershipB.Id, owner.Id, Now);
        await using (var database = new AuthDbContext(Options(runtime)))
        {
            database.AddRange(owner, organisationA, organisationB, membershipA, membershipB);
            database.Entry(owner).Property(x => x.Status).CurrentValue = UserStatus.Active;
            await database.SaveChangesAsync();
            await using var transaction = await database.Database.BeginTransactionAsync();
            await database.Database.ExecuteSqlRawAsync(
                "SELECT pg_catalog.set_config('zeka.organisation_id', {0}, true)",
                OrganisationB.ToString("D"));
            database.Add(organisationBGrant);
            await database.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var recovery = Connection(administrator, "zeka_auth_closure_recovery_test", RecoveryPassword);
        return new(administrator, migrator, runtime, recovery, owner.Id, membershipA.Id, membershipB.Id);
    }

    private static LifecycleMessageHeaderV1 Header(Guid operationId, string participant,
        long revision = 2, Guid? messageId = null, Guid? causationId = null) =>
        new(operationId, OrganisationA, revision, participant, 1,
            messageId ?? Guid.NewGuid(), causationId ?? operationId, operationId);

    private static LifecycleMessageHeaderV1 ParticipantReplyHeader(Guid operationId,
        string participant, string responsePhase = "enter-completed", long revision = 2) =>
        new(operationId, OrganisationA, revision, participant, 1,
            LifecycleMessageIdentityV1.ForPhase(operationId, participant, responsePhase, revision),
            LifecycleMessageIdentityV1.ForPhase(operationId, participant, "enter-fence", revision),
            operationId);

    private static async Task<string?> RuntimeMembershipUpdate(string connectionString,
        Guid organisationId, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisationId);
        await using var command = new NpgsqlCommand("""
            UPDATE public."OrganisationMemberships"
            SET "ConcurrencyVersion"="ConcurrencyVersion"+1 WHERE "Id"=@id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", membershipId);
        try { await command.ExecuteNonQueryAsync(); await transaction.CommitAsync(); return null; }
        catch (PostgresException exception) { await transaction.RollbackAsync(); return exception.SqlState; }
    }

    private static async Task<object?> RuntimeScalar(string connectionString,
        Guid organisationId, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisationId);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<string?> RuntimeCommandError(string connectionString,
        Guid organisationId, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisationId);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        try { await command.ExecuteNonQueryAsync(); await transaction.CommitAsync(); return null; }
        catch (PostgresException exception) { await transaction.RollbackAsync(); return exception.SqlState; }
    }

    private static async Task<string> GlobalReferenceDigest(string connectionString) =>
        (string)(await ExecuteScalar(connectionString, """
            SELECT md5(coalesce(string_agg(value, '|' ORDER BY value),'')) FROM (
              SELECT 'AspNetRoles|' || row_to_json(r)::text AS value
                FROM public."AspNetRoles" r
              UNION ALL
              SELECT 'PermissionSets|' || row_to_json(p)::text
                FROM public."PermissionSets" p
              UNION ALL
              SELECT 'LifecycleParticipantRegistryActivation|' || row_to_json(a)::text
                FROM public."LifecycleParticipantRegistryActivation" a
              UNION ALL
              SELECT 'LifecycleParticipantRegistryBindings|' || row_to_json(b)::text
                FROM public."LifecycleParticipantRegistryBindings" b
              UNION ALL
              SELECT 'LifecycleParticipantRegistryRevisions|' || row_to_json(r)::text
                FROM public."LifecycleParticipantRegistryRevisions" r
            ) digest
            """))!;

    private static async Task<string> OrganisationDigest(string connectionString,
        Guid organisationId) => (string)(await ExecuteScalar(connectionString, $"""
            SELECT md5(string_agg(value, '|' ORDER BY value)) FROM (
              SELECT row_to_json(o)::text AS value FROM public."Organisations" o
                WHERE o."Id"='{organisationId:D}'::uuid
              UNION ALL
              SELECT row_to_json(m)::text FROM public."OrganisationMemberships" m
                WHERE m."OrganisationId"='{organisationId:D}'::uuid
              UNION ALL
              SELECT row_to_json(g)::text FROM public."MembershipPermissionGrants" g
                WHERE g."OrganisationId"='{organisationId:D}'::uuid
            ) digest
            """))!;

    private static async Task<IReadOnlyDictionary<string, long>> TenantOwnedCounts(
        string connectionString, Guid organisationId)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tables = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.columns
            WHERE table_schema='public' AND column_name='OrganisationId'
            ORDER BY table_name
            """, connection);
        var names = new List<string>();
        await using (var reader = await tables.ExecuteReaderAsync())
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        foreach (var name in names)
        {
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM public.{new NpgsqlCommandBuilder().QuoteIdentifier(name)} WHERE \"OrganisationId\"=@organisation",
                connection);
            count.Parameters.AddWithValue("organisation", organisationId);
            counts[name] = (long)(await count.ExecuteScalarAsync())!;
        }
        return counts;
    }

    private static async Task<object?> ExecuteScalar(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static ServiceProvider DispatcherProvider(string runtimeConnection, TimeProvider clock,
        IOutboxMessagePublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(runtimeConnection));
        services.AddSingleton(clock);
        services.AddSingleton(publisher);
        services.AddSingleton<IOutboxMessagePublisher>(publisher);
        services.AddOnboardingPersistenceFoundations(
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> StageOutbox(string runtimeConnection, string messageType,
        string payload, Guid correlationId, DateTimeOffset now)
    {
        await using var database = new AuthDbContext(Options(runtimeConnection));
        var message = OutboxMessage.Create(messageType, LifecycleContractV1.Version, payload,
            now, now, correlationId.ToString("D"), OrganisationA);
        database.OutboxMessages.Add(message);
        await database.SaveChangesAsync();
        return message.Id;
    }

    private static async Task SetContext(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid organisationId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)",
            connection, transaction);
        command.Parameters.AddWithValue("organisation", organisationId.ToString("D"));
        await command.ExecuteScalarAsync();
    }

    private static DbContextOptions<AuthDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options;
    private static AuthClosureParticipant Participant(TestEnvironment environment, TimeProvider clock) =>
        new(Options(environment.RuntimeConnection), clock,
            new NpgsqlAuthClosureRecoveryCapability(environment.RecoveryConnection));
    private static string Connection(string administrator, string username, string password) =>
        new NpgsqlConnectionStringBuilder(administrator)
            { Username = username, Password = password, Pooling = false }.ConnectionString;
    private static async Task Execute(string connection, string sql)
    {
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, database);
        await command.ExecuteNonQueryAsync();
    }
    private static string Bootstrap()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-auth-roles.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Auth bootstrap not found.");
    }

    private sealed class CurrentAccess : ICurrentTenantAccess
    {
        public Task<CurrentTenantAccess> ResolveAsync(Guid subject, Guid organisation,
            CancellationToken cancellationToken) => Task.FromResult(new CurrentTenantAccess(
            TenantAccessOutcome.Authorized, organisation, Guid.NewGuid(), [LifecyclePermissions.Close],
            "synthetic-owner", Now));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingPublisher : IOutboxMessagePublisher
    {
        private TaskCompletionSource? _entered;
        private TaskCompletionSource? _release;
        public List<OutboxMessageEnvelope> Messages { get; } = [];

        public void BlockNext()
        {
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitUntilBlockedAsync() =>
            _entered?.Task ?? throw new InvalidOperationException("Publisher is not blocked.");

        public void Release() =>
            (_release ?? throw new InvalidOperationException("Publisher is not blocked."))
            .TrySetResult();

        public async Task PublishAsync(OutboxMessageEnvelope message,
            CancellationToken cancellationToken)
        {
            if (_entered is not null && _release is not null)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                _entered = null;
                _release = null;
            }
            Messages.Add(message);
        }
    }

    private static async Task<IReadOnlyList<CloseOrganisationParticipantV1>> ReadCloseCommands(
        string connectionString, Guid organisationId)
    {
        var result = new List<CloseOrganisationParticipantV1>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisationId);
        await using var command = new NpgsqlCommand("""
            SELECT "Payload" FROM public."OutboxMessages"
            WHERE "MessageType"='CloseOrganisationParticipantV1'
            ORDER BY "CreatedAtUtc", "Id"
            """, connection, transaction);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                result.Add(JsonSerializer.Deserialize<CloseOrganisationParticipantV1>(
                    reader.GetString(0), Json)!);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<IReadOnlyList<ReleaseOrganisationClosureFenceV1>> ReadReleaseCommands(
        string connectionString, Guid organisationId)
    {
        var result = new List<ReleaseOrganisationClosureFenceV1>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisationId);
        await using var command = new NpgsqlCommand("""
            SELECT "Payload" FROM public."OutboxMessages"
            WHERE "MessageType"='ReleaseOrganisationClosureFenceV1'
            ORDER BY "CreatedAtUtc", "Id"
            """, connection, transaction);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                result.Add(JsonSerializer.Deserialize<ReleaseOrganisationClosureFenceV1>(
                    reader.GetString(0), Json)!);
        await transaction.CommitAsync();
        return result;
    }

    private sealed class FixtureClosureContractDispatcher(
        string runtimeConnection,
        TimeProvider clock,
        LifecycleClosureCoordinator coordinator)
    {
        public async Task DispatchAsync(IEnumerable<CloseOrganisationParticipantV1> commands)
        {
            var auth = new AuthClosureParticipant(Options(runtimeConnection), clock);
            foreach (var command in commands.OrderBy(x => x.Header.ParticipantId, StringComparer.Ordinal))
            {
                OrganisationClosureParticipantCompletedV1 completed;
                if (command.Header.ParticipantId == "auth-management")
                    completed = await auth.EnterAsync(command);
                else
                {
                    var boundaryAt = clock.GetUtcNow() >= command.ClosingAt
                        ? clock.GetUtcNow() : command.ClosingAt;
                    completed = new OrganisationClosureParticipantCompletedV1(
                        new LifecycleMessageHeaderV1(command.Header.OperationId,
                            command.Header.OrganisationId, command.Header.OperationRevision,
                            command.Header.ParticipantId, command.Header.ContractVersion,
                            LifecycleMessageIdentityV1.ForPhase(command.Header.OperationId,
                                command.Header.ParticipantId, "enter-completed",
                                command.Header.OperationRevision), command.Header.MessageId,
                            command.Header.CorrelationId),
                        $"fixture-{command.Header.ParticipantId}-fence", 1, boundaryAt);
                }
                var result = await coordinator.ReceiveAsync(completed);
                Assert.Contains(result.Status, [ClosureProgressStatus.AwaitingParticipants,
                    ClosureProgressStatus.Archived, ClosureProgressStatus.Replay]);
            }
        }
    }

    private sealed record TestEnvironment(string AdministratorConnection,
        string MigratorConnection, string RuntimeConnection, string RecoveryConnection,
        Guid OwnerUserId, Guid OwnerMembershipA, Guid OwnerMembershipB);
}

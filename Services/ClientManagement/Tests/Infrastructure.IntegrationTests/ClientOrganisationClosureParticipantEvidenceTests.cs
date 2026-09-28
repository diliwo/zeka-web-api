using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Common.Behaviours;
using ClientManagement.Application.Clients.Commands.AddClient;
using ClientManagement.Application.Lifecycle;
using ClientManagement.Infrastructure.Messaging;
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
using Zeka.Contracts.Staff.V1;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "ProviderReal")]
public sealed class ClientOrganisationClosureParticipantEvidenceTests(PostgreSqlClientRuntimeDatabase database)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    private static readonly string[] OrdinaryOwnedTables =
    [
        "Assessments", "Clients", "MonitoringReports", "ProfessionalAssessments",
        "ProfessionnalExperience", "SchoolRegistrations", "SocialCases", "SocialWorkers"
    ];

    private static readonly string[] GlobalReferenceTables =
    [
        "Languages", "MonitoringActions", "NatureOfContract", "Profession", "School",
        "Training", "TrainingField", "TrainingType"
    ];

    [Fact]
    public async Task Closure_boundary_is_durable_replayable_tenant_isolated_and_technically_releasable()
    {
        var organisation = Guid.NewGuid();
        var otherOrganisation = Guid.NewGuid();
        await using var providerA = Provider(organisation);
        await using var providerB = Provider(organisation);
        await SeedClient(providerA, organisation, 31);
        await SeedClient(providerA, otherOrganisation, 32);
        var beforeA = await SnapshotOwnedTables(organisation);
        var beforeB = await SnapshotOwnedTables(otherOrganisation);
        var beforeGlobal = await SnapshotGlobalReferenceTables();

        var operation = Guid.NewGuid();
        var command = new CloseOrganisationParticipantV1(
            Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow);
        var completed = await Invoke(providerA, organisation,
            participant => participant.EnterFenceAsync(command));
        var replay = await Invoke(providerB, organisation,
            participant => participant.EnterFenceAsync(command));
        Assert.Equal(completed.Header.MessageId, replay.Header.MessageId);
        Assert.Equal(completed.ReceiptHash, replay.ReceiptHash);

        var application = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteActualAddClientCommand(providerA, organisation, 33));
        Assert.Equal("organisation_closure_fence_active", application.Message);

        var direct = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSql(organisation,
            "UPDATE public.\"Clients\" SET \"ReferenceNumber\" = \"ReferenceNumber\""));
        Assert.Equal("organisation_closure_fence_active", direct.MessageText);
        var rawRelease = await Assert.ThrowsAsync<PostgresException>(() => RuntimeSql(organisation,
            "UPDATE public.\"OrganisationClosureFences\" SET \"ReleasedAt\" = now()"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rawRelease.SqlState);
        await InTenant(providerA, organisation, async services =>
        {
            await services.GetRequiredService<ITenantTransactionExecutor>().ExecuteLifecycleAsync(async token =>
            {
                var context = services.GetRequiredService<ApplicationDbContext>();
                var fence = await context.OrganisationClosureFences.AsNoTracking().SingleAsync(token);
                var inbox = await context.OrganisationClosureInbox.AsNoTracking().SingleAsync(token);
                var outbox = await context.OrganisationClosureOutbox.AsNoTracking().SingleAsync(token);
                Assert.Equal(operation, fence.OperationId);
                Assert.Equal(command.PayloadHash, fence.RequestHash);
                Assert.Equal(command.Header.MessageId, inbox.MessageId);
                Assert.Equal(completed.Header.MessageId, outbox.MessageId);
            }, default);
        });

        var releaseHeader = RecoveryHeader(command.Header, completed.Header.MessageId, Guid.Empty);
        var wrongMessage = await Assert.ThrowsAsync<PostgresException>(() => RuntimeReleaseFunction(
            releaseHeader, completed.FenceToken, completed.BoundaryEstablishedAt, Guid.NewGuid()));
        Assert.Equal("organisation_closure_release_message_invalid", wrongMessage.MessageText);
        var wrongToken = await Assert.ThrowsAsync<PostgresException>(() => RuntimeReleaseFunction(
            releaseHeader, "forged", completed.BoundaryEstablishedAt));
        Assert.Equal("organisation_closure_fence_release_conflict", wrongToken.MessageText);
        Assert.Equal("organisation_closure_fence_active",
            (await Assert.ThrowsAsync<PostgresException>(() => RuntimeSql(organisation,
                "UPDATE public.\"Clients\" SET \"ReferenceNumber\" = \"ReferenceNumber\""))).MessageText);

        var release = new ReleaseOrganisationClosureFenceV1(releaseHeader, completed.FenceToken);
        var released = await Invoke(providerA, organisation,
            participant => participant.ReleaseFenceAsync(release));
        var releasedReplay = await Invoke(providerB, organisation,
            participant => participant.ReleaseFenceAsync(release));
        Assert.Equal(released.Header.MessageId, releasedReplay.Header.MessageId);
        Assert.Equal(released.ReceiptHash, releasedReplay.ReceiptHash);

        AssertSnapshotsEqual(beforeA, await SnapshotOwnedTables(organisation));
        AssertSnapshotsEqual(beforeB, await SnapshotOwnedTables(otherOrganisation));
        AssertSnapshotsEqual(beforeGlobal, await SnapshotGlobalReferenceTables());
        await SeedClient(providerA, organisation, 35);

        Assert.Equal(beforeA["Clients"].Count + 1, await CountRows("Clients", organisation));
        AssertSnapshotsEqual(beforeB, await SnapshotOwnedTables(otherOrganisation));
        AssertSnapshotsEqual(beforeGlobal, await SnapshotGlobalReferenceTables());
    }

    [Fact]
    public async Task Lagging_submicrosecond_clocks_preserve_causal_canonical_receipts_across_reload()
    {
        var organisation = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var rawClosingAt = new DateTimeOffset(
            (DateTimeOffset.UtcNow.Ticks / 10 * 10) + 7,
            TimeSpan.Zero).AddMinutes(5);
        var enterClock = new FixedTimeProvider(rawClosingAt.AddHours(-1).AddTicks(2));
        var enter = new CloseOrganisationParticipantV1(
            Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid()), rawClosingAt);

        OrganisationClosureParticipantCompletedV1 completed;
        await using (var provider = Provider(organisation, enterClock))
            completed = await Invoke(provider, organisation,
                participant => participant.EnterFenceAsync(enter));

        Assert.Equal(enter.ClosingAt, completed.BoundaryEstablishedAt);
        Assert.True(completed.BoundaryEstablishedAt >= enter.ClosingAt);
        Assert.Equal(0, completed.BoundaryEstablishedAt.Ticks % 10);

        var releaseClock = new FixedTimeProvider(
            completed.BoundaryEstablishedAt.AddHours(-1).AddTicks(9));
        await using var reloadedProvider = Provider(organisation, releaseClock);
        var reloadedCompletion = await ReadReceipt<OrganisationClosureParticipantCompletedV1>(
            reloadedProvider, organisation, enter.Header.MessageId);
        var enterReplay = await Invoke(reloadedProvider, organisation,
            participant => participant.EnterFenceAsync(enter));
        Assert.Equal(completed.Header.MessageId, reloadedCompletion.Header.MessageId);
        Assert.Equal(completed.BoundaryEstablishedAt, reloadedCompletion.BoundaryEstablishedAt);
        Assert.Equal(completed.ReceiptHash, reloadedCompletion.ReceiptHash);
        Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(enterReplay));

        var release = new ReleaseOrganisationClosureFenceV1(
            RecoveryHeader(enter.Header, completed.Header.MessageId, Guid.Empty), completed.FenceToken);
        var released = await Invoke(reloadedProvider, organisation,
            participant => participant.ReleaseFenceAsync(release));
        Assert.True(released.ReleasedAt >= completed.BoundaryEstablishedAt);
        Assert.Equal(completed.BoundaryEstablishedAt, released.ReleasedAt);
        Assert.Equal(0, released.ReleasedAt.Ticks % 10);

        await using var secondReload = Provider(organisation,
            new FixedTimeProvider(released.ReleasedAt.AddHours(-2).AddTicks(3)));
        var reloadedRelease = await ReadReceipt<OrganisationClosureFenceReleasedV1>(
            secondReload, organisation, release.Header.MessageId);
        var releaseReplay = await Invoke(secondReload, organisation,
            participant => participant.ReleaseFenceAsync(release));
        Assert.Equal(released.Header.MessageId, reloadedRelease.Header.MessageId);
        Assert.Equal(released.ReleasedAt, reloadedRelease.ReleasedAt);
        Assert.Equal(released.ReceiptHash, reloadedRelease.ReceiptHash);
        Assert.Equal(JsonSerializer.Serialize(released), JsonSerializer.Serialize(releaseReplay));
    }

    [Fact]
    public async Task Every_owned_write_surface_has_a_closure_trigger_and_lifecycle_evidence_is_append_preserving()
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT trigger.tgname FROM pg_catalog.pg_trigger trigger
            JOIN pg_catalog.pg_class relation ON relation.oid = trigger.tgrelid
            JOIN pg_catalog.pg_namespace schema ON schema.oid = relation.relnamespace
            WHERE NOT trigger.tgisinternal AND schema.nspname = 'public'
              AND trigger.tgname LIKE 'trg_%_closure_fence' ORDER BY trigger.tgname
            """, connection);
        var actual = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) actual.Add(reader.GetString(0));
        Assert.Equal(new[]
        {
            "trg_assessments_closure_fence", "trg_clients_closure_fence",
            "trg_monitoringreports_closure_fence", "trg_professionalassessments_closure_fence",
            "trg_professionnalexperience_closure_fence", "trg_schoolregistrations_closure_fence",
            "trg_socialcases_closure_fence", "trg_socialworkers_closure_fence"
        }, actual);

        var organisation = Guid.NewGuid();
        await using var provider = Provider(organisation);
        var operation = Guid.NewGuid();
        var enter = new CloseOrganisationParticipantV1(
            Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow);
        var completed = await Invoke(provider, organisation, participant => participant.EnterFenceAsync(enter));
        foreach (var sql in new[]
        {
            "UPDATE public.\"OrganisationClosureFences\" SET \"FenceToken\" = 'forged'",
            "DELETE FROM public.\"OrganisationClosureInbox\"",
            "UPDATE public.\"OrganisationClosureOutbox\" SET \"PublishedAt\" = now()"
        })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await Assert.ThrowsAsync<PostgresException>(() => RuntimeSql(organisation, sql))).SqlState);

        await Invoke(provider, organisation, participant => participant.ReleaseFenceAsync(
            new ReleaseOrganisationClosureFenceV1(
                RecoveryHeader(enter.Header, completed.Header.MessageId, Guid.Empty), completed.FenceToken)));
    }

    [Fact]
    public async Task Boundary_waits_for_pre_boundary_transaction_and_rejects_all_later_writes()
    {
        var organisation = Guid.NewGuid();
        await using var provider = Provider(organisation);
        await SeedClient(provider, organisation, 41);
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisation);
        await using (var insert = new NpgsqlCommand("""
            UPDATE public."Clients" SET "ReferenceNumber" = 'pre-boundary'
            WHERE "OrganisationId" = @organisation
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("organisation", organisation);
            await insert.ExecuteNonQueryAsync();
        }

        var enterTask = Invoke(provider, organisation, participant => participant.EnterFenceAsync(
            new CloseOrganisationParticipantV1(
                Header(Guid.NewGuid(), organisation, Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow)));
        await Task.Delay(150);
        Assert.False(enterTask.IsCompleted);
        await transaction.CommitAsync();
        var preBoundaryCommittedAt = DateTimeOffset.UtcNow;
        var completed = await enterTask;
        Assert.True(completed.BoundaryEstablishedAt >= preBoundaryCommittedAt,
            $"Boundary {completed.BoundaryEstablishedAt:O} preceded committed ordinary work {preBoundaryCommittedAt:O}.");

        Assert.Equal(1, await CountRows("Clients", organisation));
        await Assert.ThrowsAsync<PostgresException>(() => RuntimeSql(organisation,
            "UPDATE public.\"Clients\" SET \"ReferenceNumber\" = 'post-boundary'"));
    }

    [Fact]
    public async Task Conflicts_cross_tenant_identity_and_production_registration_fail_closed()
    {
        var organisation = Guid.NewGuid();
        await using (var production = ProductionProvider())
            Assert.Null(production.GetService<IClientOrganisationClosureParticipant>());
        await using var provider = Provider(organisation);
        var first = new CloseOrganisationParticipantV1(
            Header(Guid.NewGuid(), organisation, Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow);
        var entered = await Invoke(provider, organisation, participant => participant.EnterFenceAsync(first));

        var conflicting = new CloseOrganisationParticipantV1(
            Header(Guid.NewGuid(), organisation, Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow);
        Assert.Equal("organisation_closure_fence_active",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
                participant => participant.EnterFenceAsync(conflicting)))).Message);

        var wrongTenant = Guid.NewGuid();
        var acceptedRecoveryHeader = RecoveryHeader(first.Header, entered.Header.MessageId, Guid.Empty);
        var wrongTenantHeader = new LifecycleMessageHeaderV1(
            acceptedRecoveryHeader.OperationId,
            wrongTenant,
            acceptedRecoveryHeader.OperationRevision,
            acceptedRecoveryHeader.ParticipantId,
            acceptedRecoveryHeader.ContractVersion,
            acceptedRecoveryHeader.MessageId,
            acceptedRecoveryHeader.CausationId,
            acceptedRecoveryHeader.CorrelationId);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
            participant => participant.ReleaseFenceAsync(new ReleaseOrganisationClosureFenceV1(
                wrongTenantHeader, entered.FenceToken))));
        Assert.Equal("client_closure_nonfixture_scope_rejected", rejected.Message);

        Assert.Equal("organisation_closure_fence_token_invalid",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
                participant => participant.ReleaseFenceAsync(new ReleaseOrganisationClosureFenceV1(
                    RecoveryHeader(first.Header, entered.Header.MessageId, Guid.Empty), "forged"))))).Message);

        var staleRevision = new ReleaseOrganisationClosureFenceV1(
            RecoveryHeader(first.Header, entered.Header.MessageId, Guid.Empty, revision: 1), entered.FenceToken);
        Assert.Equal("organisation_closure_revision_conflict",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
                participant => participant.ReleaseFenceAsync(staleRevision)))).Message);

        var skippedRevision = new ReleaseOrganisationClosureFenceV1(
            RecoveryHeader(first.Header, entered.Header.MessageId, Guid.Empty, revision: 3), entered.FenceToken);
        Assert.Equal("organisation_closure_revision_conflict",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
                participant => participant.ReleaseFenceAsync(skippedRevision)))).Message);

        var wrongCausation = new ReleaseOrganisationClosureFenceV1(
            RecoveryHeader(first.Header, Guid.NewGuid(), Guid.Empty), entered.FenceToken);
        Assert.Equal("organisation_closure_recovery_identity_conflict",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
                participant => participant.ReleaseFenceAsync(wrongCausation)))).Message);

        var wrongCorrelationHeader = RecoveryHeader(first.Header, entered.Header.MessageId, Guid.Empty);
        var wrongCorrelation = new ReleaseOrganisationClosureFenceV1(
            new LifecycleMessageHeaderV1(
                wrongCorrelationHeader.OperationId,
                wrongCorrelationHeader.OrganisationId,
                wrongCorrelationHeader.OperationRevision,
                wrongCorrelationHeader.ParticipantId,
                wrongCorrelationHeader.ContractVersion,
                wrongCorrelationHeader.MessageId,
                wrongCorrelationHeader.CausationId,
                Guid.NewGuid()),
            entered.FenceToken);
        Assert.Equal("organisation_closure_recovery_identity_conflict",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
                participant => participant.ReleaseFenceAsync(wrongCorrelation)))).Message);

        var acceptedRelease = new ReleaseOrganisationClosureFenceV1(
            RecoveryHeader(first.Header, entered.Header.MessageId, Guid.Empty), entered.FenceToken);
        var released = await Invoke(provider, organisation,
            participant => participant.ReleaseFenceAsync(acceptedRelease));
        var releaseReplay = await Invoke(provider, organisation,
            participant => participant.ReleaseFenceAsync(acceptedRelease));
        Assert.Equal(released.Header.MessageId, releaseReplay.Header.MessageId);
        Assert.Equal(released.ReceiptHash, releaseReplay.ReceiptHash);

        var conflictingReplayIdentity = new ReleaseOrganisationClosureFenceV1(
            RecoveryHeader(first.Header, entered.Header.MessageId, Guid.NewGuid()), entered.FenceToken);
        Assert.Equal("organisation_closure_participant_identity_invalid",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
                participant => participant.ReleaseFenceAsync(conflictingReplayIdentity)))).Message);
    }

    [Fact]
    public async Task Enter_lineage_is_canonical_before_persistence_and_legitimate_delivery_replays()
    {
        var organisation = Guid.NewGuid();
        var operation = Guid.NewGuid();
        await using var provider = Provider(organisation);
        var header = Header(operation, organisation, Guid.NewGuid(), Guid.NewGuid());
        var closingAt = DateTimeOffset.UtcNow;

        var wrongCausation = new LifecycleMessageHeaderV1(
            header.OperationId, header.OrganisationId, header.OperationRevision, header.ParticipantId,
            header.ContractVersion, header.MessageId, Guid.NewGuid(), header.CorrelationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
            participant => participant.EnterFenceAsync(new(wrongCausation, closingAt))));
        Assert.Equal((0, 0, 0), await ClosureRecordCounts(provider, organisation, operation));

        var wrongCorrelation = new LifecycleMessageHeaderV1(
            header.OperationId, header.OrganisationId, header.OperationRevision, header.ParticipantId,
            header.ContractVersion, header.MessageId, header.CausationId, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(provider, organisation,
            participant => participant.EnterFenceAsync(new(wrongCorrelation, closingAt))));
        Assert.Equal((0, 0, 0), await ClosureRecordCounts(provider, organisation, operation));

        var legitimate = new CloseOrganisationParticipantV1(header, closingAt);
        var completed = await Invoke(provider, organisation,
            participant => participant.EnterFenceAsync(legitimate));
        var replay = await Invoke(provider, organisation,
            participant => participant.EnterFenceAsync(legitimate));
        Assert.Equal(completed, replay);
        Assert.Equal((1, 1, 1), await ClosureRecordCounts(provider, organisation, operation));
    }

    [Fact]
    public async Task Staff_projection_consumer_is_denied_after_the_durable_closure_boundary()
    {
        var organisation = Guid.NewGuid();
        var membership = Guid.NewGuid();
        var handler = new ClosureConsumerAccessHandler(organisation, membership);
        var configuration = RuntimeConfiguration();
        var services = ProductionServices(configuration);
        services.AddSingleton<IHttpClientFactory>(new ClosureConsumerHttpClientFactory(handler));
        services.AddClientClosureFixtureParticipant(organisation);
        await using var provider = services.BuildServiceProvider();

        var enter = new CloseOrganisationParticipantV1(
            Header(Guid.NewGuid(), organisation, Guid.NewGuid(), Guid.NewGuid()), DateTimeOffset.UtcNow);
        await Invoke(provider, organisation, participant => participant.EnterFenceAsync(enter));

        var consumer = new StaffProjectionConsumer(
            provider.GetRequiredService<IServiceScopeFactory>(), configuration);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Handle(
            new StaffProjectionChangedV1(organisation, membership, 1, true,
                "Closure", "Evidence", "closure-worker", "Team", "T")));
        Assert.Equal("organisation_closure_fence_active", rejected.Message);
        Assert.Equal(0, await CountRows("SocialWorkers", organisation));
    }

    private ServiceProvider Provider(Guid fixtureOrganisation, TimeProvider? clock = null)
    {
        var services = ProductionServices();
        if (clock is not null)
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(clock);
        }
        services.AddClientClosureFixtureParticipant(fixtureOrganisation);
        return services.BuildServiceProvider();
    }

    private ServiceProvider ProductionProvider() => ProductionServices().BuildServiceProvider();

    private IServiceCollection ProductionServices(ConfigurationManager? configuration = null)
    {
        configuration ??= RuntimeConfiguration();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        services.RemoveAll<IHostedService>();
        return services;
    }

    private ConfigurationManager RuntimeConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/",
            ["TenantWorker:SubjectId"] = "life-02-worker",
            ["TenantWorker:BearerToken"] = "synthetic-life-02-token",
            ["FileServerPath"] = Path.GetTempPath()
        });
        return configuration;
    }

    private async Task SeedClient(IServiceProvider provider, Guid organisation, int sequence) =>
        await InTenant(provider, organisation, services =>
            services.GetRequiredService<ITenantTransactionExecutor>().ExecuteOrdinaryAsync(async token =>
            {
                var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                    ClientManagement.Tests.Common.SyntheticClient.Niss(sequence));
                client.ReferenceNumber = $"closure-reference-{sequence:D2}";
                services.GetRequiredService<ApplicationDbContext>().Clients.Add(client);
                await services.GetRequiredService<ApplicationDbContext>().SaveChangesAsync(token);
            }, default));

    private async Task<int> ExecuteActualAddClientCommand(
        IServiceProvider provider,
        Guid organisation,
        int sequence)
    {
        await using var scope = provider.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContextScope>();
        var membership = new AuthorizedTenantMembership(
            organisation,
            Guid.NewGuid(),
            ["Clients.Create"],
            "life-02-client-evidence",
            DateTimeOffset.UtcNow);
        var operation = new TenantOperation(
            new FixedTenantAccess(membership),
            new FixedOperationIdentity(organisation),
            tenant,
            tenant);
        var behavior = new AuthorizationBehaviour<AddClientCommand, int>(operation,
            scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>());
        return await behavior.Handle(new AddClientCommand(), async () =>
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                ClientManagement.Tests.Common.SyntheticClient.Niss(sequence));
            context.Clients.Add(client);
            await context.SaveChangesAsync();
            return client.Id;
        }, default);
    }

    private async Task RuntimeSql(Guid organisation, string sql)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisation);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private async Task RuntimeReleaseFunction(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        DateTimeOffset releasedAt,
        Guid? messageId = null)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, header.OrganisationId);
        await using var command = new NpgsqlCommand("""
            SELECT zeka.release_organisation_closure_fence(
              @organisation_id, @operation_id, @participant_id, @operation_revision,
              @fence_token, @contract_version, @release_message_id, @causation_id,
              @correlation_id, @released_at)
            """, connection, transaction);
        command.Parameters.AddWithValue("organisation_id", header.OrganisationId);
        command.Parameters.AddWithValue("operation_id", header.OperationId);
        command.Parameters.AddWithValue("participant_id", header.ParticipantId);
        command.Parameters.AddWithValue("operation_revision", header.OperationRevision);
        command.Parameters.AddWithValue("fence_token", fenceToken);
        command.Parameters.AddWithValue("contract_version", header.ContractVersion);
        command.Parameters.AddWithValue("release_message_id", messageId ?? header.MessageId);
        command.Parameters.AddWithValue("causation_id", header.CausationId);
        command.Parameters.AddWithValue("correlation_id", header.CorrelationId);
        command.Parameters.AddWithValue("released_at", releasedAt);
        await command.ExecuteScalarAsync();
    }

    private static async Task<T> ReadReceipt<T>(
        IServiceProvider provider,
        Guid organisation,
        Guid messageId) where T : class
    {
        T result = default!;
        await InTenant(provider, organisation, services =>
            services.GetRequiredService<ITenantTransactionExecutor>().ExecuteLifecycleAsync(async token =>
            {
                var receipt = await services.GetRequiredService<ApplicationDbContext>()
                    .OrganisationClosureInbox.AsNoTracking()
                    .SingleAsync(value => value.MessageId == messageId, token);
                result = JsonSerializer.Deserialize<T>(
                    receipt.ResponseJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Persisted closure receipt could not be reloaded.");
            }, default));
        return result;
    }

    private async Task<long> CountRows(string table, Guid organisation)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisation);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM public.\"{table}\" WHERE \"OrganisationId\" = @organisation", connection, transaction);
        command.Parameters.AddWithValue("organisation", organisation);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyDictionary<string, TableDigest>> SnapshotOwnedTables(Guid organisation)
    {
        var snapshot = new SortedDictionary<string, TableDigest>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContext(connection, transaction, organisation);
        foreach (var table in OrdinaryOwnedTables)
            snapshot.Add(table, await ReadDigest(connection, transaction,
                $"SELECT row_to_json(value)::text FROM public.\"{table}\" value "
                + "WHERE \"OrganisationId\" = @organisation ORDER BY row_to_json(value)::text",
                organisation));
        await transaction.RollbackAsync();
        return snapshot;
    }

    private async Task<IReadOnlyDictionary<string, TableDigest>> SnapshotGlobalReferenceTables()
    {
        var snapshot = new SortedDictionary<string, TableDigest>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        foreach (var table in GlobalReferenceTables)
            snapshot.Add(table, await ReadDigest(connection, null,
                $"SELECT row_to_json(value)::text FROM public.\"{table}\" value "
                + "ORDER BY row_to_json(value)::text"));
        return snapshot;
    }

    private static async Task<TableDigest> ReadDigest(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        Guid? organisation = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (organisation is not null) command.Parameters.AddWithValue("organisation", organisation.Value);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        using var canonical = new MemoryStream();
        foreach (var row in rows)
        {
            var bytes = Encoding.UTF8.GetBytes(row);
            var prefix = Encoding.ASCII.GetBytes($"{bytes.Length}:");
            canonical.Write(prefix);
            canonical.Write(bytes);
        }
        return new TableDigest(rows.Count,
            Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant());
    }

    private static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, TableDigest> expected,
        IReadOnlyDictionary<string, TableDigest> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var table in expected.Keys) Assert.Equal(expected[table], actual[table]);
    }

    private static async Task SetContext(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organisation)
    {
        await using var context = new NpgsqlCommand(
            "SELECT set_config('zeka.organisation_id', @organisation, true)", connection, transaction);
        context.Parameters.AddWithValue("organisation", organisation.ToString("D"));
        await context.ExecuteNonQueryAsync();
    }

    private static async Task<T> Invoke<T>(IServiceProvider provider, Guid organisation,
        Func<IClientOrganisationClosureParticipant, Task<T>> action)
    {
        T result = default!;
        await InTenant(provider, organisation, async services =>
            result = await action(services.GetRequiredService<IClientOrganisationClosureParticipant>()));
        return result;
    }

    private static async Task InTenant(IServiceProvider provider, Guid organisation,
        Func<IServiceProvider, Task> action)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "life-02-client-evidence"));
        await action(scope.ServiceProvider);
    }

    private static LifecycleMessageHeaderV1 Header(
        Guid operation,
        Guid organisation,
        Guid message,
        Guid correlation) =>
        new(operation, organisation, 1, ClientClosureContract.ParticipantId, LifecycleContractV1.Version,
            LifecycleMessageIdentityV1.ForPhase(
                operation, ClientClosureContract.ParticipantId, "enter-fence", 1),
            operation, operation);

    private static async Task<(int Fences, int Inbox, int Outbox)> ClosureRecordCounts(
        IServiceProvider provider, Guid organisation, Guid operation)
    {
        var result = (Fences: 0, Inbox: 0, Outbox: 0);
        await InTenant(provider, organisation, services =>
            services.GetRequiredService<ITenantTransactionExecutor>().ExecuteLifecycleAsync(async token =>
            {
                var context = services.GetRequiredService<ApplicationDbContext>();
                result = (
                    await context.OrganisationClosureFences.CountAsync(x => x.OperationId == operation, token),
                    await context.OrganisationClosureInbox.CountAsync(x => x.OperationId == operation, token),
                    await context.OrganisationClosureOutbox.CountAsync(x => x.OperationId == operation, token));
            }, default));
        return result;
    }

    private static LifecycleMessageHeaderV1 RecoveryHeader(
        LifecycleMessageHeaderV1 enter,
        Guid acceptedEnterReceiptMessageId,
        Guid messageId,
        long? revision = null) =>
        new(enter.OperationId, enter.OrganisationId, revision ?? checked(enter.OperationRevision + 1),
            ClientClosureContract.ParticipantId, LifecycleContractV1.Version,
            messageId == Guid.Empty
                ? LifecycleMessageIdentityV1.ForPhase(
                    enter.OperationId,
                    ClientClosureContract.ParticipantId,
                    "release-fence",
                    revision ?? checked(enter.OperationRevision + 1))
                : messageId,
            acceptedEnterReceiptMessageId, enter.CorrelationId);

    private sealed record TableDigest(int Count, string Sha256);

    private sealed record FixedOperationIdentity(Guid SelectedOrganisationId) : IOperationIdentity
    {
        public string SubjectId => "life-02-client-evidence";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FixedTenantAccess(AuthorizedTenantMembership membership) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(
            string authenticatedSubjectId,
            Guid selectedOrganisationId,
            CancellationToken cancellationToken) => Task.FromResult(
            new TenantAccessDecision(TenantAccessOutcome.Authorized, membership));
    }

    private sealed class ClosureConsumerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ClosureConsumerAccessHandler(Guid organisation, Guid membership) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("synthetic-life-02-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.Contains("/memberships/", StringComparison.Ordinal))
                return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith(membership.ToString(),
                    StringComparison.OrdinalIgnoreCase)
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : new HttpResponseMessage(HttpStatusCode.Forbidden));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    ContractVersion = 1,
                    SubjectId = "life-02-worker",
                    OrganisationId = organisation,
                    OrganisationMembershipId = Guid.NewGuid(),
                    EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" },
                    DecisionVersion = "life-02",
                    ObservedAtUtc = DateTimeOffset.UtcNow
                })
            });
        }
    }
}

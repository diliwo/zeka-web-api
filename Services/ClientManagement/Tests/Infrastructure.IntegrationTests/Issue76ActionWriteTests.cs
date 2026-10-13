using ClientManagement.Application;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Common.Behaviours;
using ClientManagement.Application.Supports.Commands.UpsertSupport;
using ClientManagement.Application.Supports.Commands.CloseTrack;
using ClientManagement.Application.Supports.Commands.DeleteSupport;
using ClientManagement.Application.Supports.Commands.SendReferentChangedNotification;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using ClientManagement.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
using Zeka.Extensions.MultiTenancy.AspNetCore;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "76")]
[Trait("Evidence", "ProviderReal")]
public sealed class Issue76ActionWriteTests(PostgreSqlClientRuntimeDatabase database)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    private static int sequence = 150;

    [Fact]
    public async Task Record_interaction_is_atomic_idempotent_and_tenant_non_disclosing()
    {
        var organisation = Guid.NewGuid(); var other = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var own = await Seed(organisation, actor);
        var hidden = await Seed(other, Guid.NewGuid());
        await using var provider = Provider(organisation, actor);
        var command = new RecordInteractionCommand(own.ClientId, own.CaseId, DateTime.UtcNow,
            "Synthetic contact note", "record-1");
        var first = await Send(provider, command);
        var retry = await Send(provider, command);
        Assert.Equal(first, retry);
        Assert.Equal(1, first.Revision);
        Assert.Null(first.FollowUpActionId);
        await Assert.ThrowsAsync<ActionWriteConflictException>(() => Send(provider,
            command with { Note = "Different synthetic note" }));
        await Assert.ThrowsAsync<TenantAccessException>(() => Send(provider,
            command with { ClientId = hidden.ClientId, SocialCaseId = hidden.CaseId, IdempotencyKey = "other-tenant" }));
        Assert.Equal(1, await Count<Interaction>(organisation));
        Assert.Equal(1, await Count<IdempotencyReceipt>(organisation));
        Assert.Equal(0, await Count<Interaction>(other));
    }

    [Fact]
    public async Task Production_registration_denies_sensitive_action_writes_without_partial_rows()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var seed = await Seed(organisation, actor);
        await using var provider = Provider(organisation, actor);
        using (var scope = provider.CreateScope())
            Assert.IsType<ClosedActionWriteActivationGate>(
                scope.ServiceProvider.GetRequiredService<IActionWriteActivationGate>());
        var command = new RecordInteractionCommand(seed.ClientId, seed.CaseId, DateTime.UtcNow,
            "Synthetic linked note", "blocked-action",
            new FollowUpActionInput("Follow up", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), actor));
        await Assert.ThrowsAsync<TenantAccessException>(() => Send(provider, command));
        await Assert.ThrowsAsync<TenantAccessException>(() => Send(provider,
            new ActionChangeCommand(1, 1, "blocked-change", ActionChangeKind.Complete)));
        Assert.Equal(0, await Count<Interaction>(organisation));
        Assert.Equal(0, await Count<FollowUpAction>(organisation));
        Assert.Equal(0, await Count<IdempotencyReceipt>(organisation));
    }

    [Fact]
    public void Production_api_exposes_no_interaction_or_follow_up_action_route()
    {
        var assembly = typeof(ClientManagement.API.Controllers.SupportsController).Assembly;
        var methods = assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToArray();
        Assert.NotEmpty(methods);
        Assert.DoesNotContain(methods, method => method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(RecordInteractionCommand)
            || parameter.ParameterType == typeof(ActionChangeCommand)));
        Assert.DoesNotContain(methods, method => method.GetCustomAttributes<HttpMethodAttribute>()
            .SelectMany(attribute => attribute.HttpMethods.Select(_ => attribute.Template ?? string.Empty))
            .Any(template => template.Contains("interaction", StringComparison.OrdinalIgnoreCase)
                || template.Contains("follow-up-action", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Simulated_indeterminate_commit_reconciles_receipt_without_replaying_handler()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var scope = new Zeka.Extensions.MultiTenancy.Abstractions.TenantContextScope();
        var operation = new TenantOperation(new Access(organisation, actor, true),
            new Identity(organisation), scope, scope);
        var expected = new RecordInteractionResult(47, 1, null);
        var executor = new UncertainOnceExecutor();
        var behavior = new AuthorizationBehaviour<RecordInteractionCommand, RecordInteractionResult>(
            operation, executor, new PostCommitNotifications(), new ReconciledReceiptStore(expected));
        var handlerCalls = 0;
        var result = await behavior.Handle(new RecordInteractionCommand(1, 1, DateTime.UtcNow,
            "Synthetic note", "uncertain-1"), () =>
        {
            handlerCalls++;
            return Task.FromResult(expected);
        }, default);
        Assert.Equal(expected, result);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(2, executor.Attempts); // Mutation attempt, then guarded receipt read.
    }

    [Fact]
    public async Task Assigned_editor_cannot_write_unassigned_dossier_and_current_context_is_required()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var own = await Seed(organisation, actor);
        var other = await Seed(organisation, Guid.NewGuid());
        await using var assigned = Provider(organisation, actor, all: false);
        var occurred = DateTime.UtcNow;
        Assert.True((await Send(assigned, new RecordInteractionCommand(own.ClientId, own.CaseId,
            occurred, "Assigned synthetic note", "assigned-1"))).InteractionId > 0);
        await Assert.ThrowsAsync<TenantAccessException>(() => Send(assigned,
            new RecordInteractionCommand(other.ClientId, other.CaseId,
                occurred, "Hidden synthetic note", "hidden-1")));

        var noCaseOrganisation = Guid.NewGuid(); var noCaseActor = Guid.NewGuid();
        var noCase = await Seed(noCaseOrganisation, noCaseActor);
        await SetCaseEndDate(noCaseOrganisation, noCase.CaseId, DateTime.Today.AddDays(-1));
        await using var all = Provider(noCaseOrganisation, noCaseActor);
        var required = await Assert.ThrowsAsync<ActionWriteConflictException>(() => Send(all,
            new RecordInteractionCommand(noCase.ClientId, noCase.CaseId,
                occurred, "No current case", "no-case")));
        Assert.Equal("CONTEXT_REQUIRED", required.Code);

        var conflictOrganisation = Guid.NewGuid(); var conflictActor = Guid.NewGuid();
        var conflict = await Seed(conflictOrganisation, conflictActor);
        await AddOverlappingCase(conflictOrganisation, conflict);
        await using var conflictProvider = Provider(conflictOrganisation, conflictActor);
        var result = await Assert.ThrowsAsync<ActionWriteConflictException>(() => Send(conflictProvider,
            new RecordInteractionCommand(conflict.ClientId, conflict.CaseId,
                occurred, "Ambiguous current case", "two-cases")));
        Assert.Equal("CONTEXT_CONFLICT", result.Code);
        Assert.Equal(0, await Count<Interaction>(conflictOrganisation));
    }

    [Fact]
    public async Task Synthetic_internal_action_contract_checks_version_and_attributed_history()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var seed = await Seed(organisation, actor);
        await using var provider = Provider(organisation, actor, syntheticActionGate: true);
        var command = new RecordInteractionCommand(seed.ClientId, seed.CaseId, DateTime.UtcNow,
            "Synthetic action-linked contact", "linked-1",
            new FollowUpActionInput("Submit application", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), actor));
        var result = await Send(provider, command);
        Assert.NotNull(result.FollowUpActionId);
        Assert.Equal(result, await Send(provider, command));
        var actionId = result.FollowUpActionId!.Value;
        var due = new ActionChangeCommand(actionId, 1, "due-1", ActionChangeKind.ChangeDueDate,
            NewDueLocalDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        var dueResult = await Send(provider, due);
        Assert.Equal(2, dueResult.Version);
        Assert.Equal(dueResult, await Send(provider, due));
        await Assert.ThrowsAsync<ActionWriteConflictException>(() => Send(provider,
            due with { NewDueLocalDate = due.NewDueLocalDate!.Value.AddDays(1) }));
        var reassign = new ActionChangeCommand(actionId, 2, "reassign-1", ActionChangeKind.Reassign,
            NewAssigneeMembershipId: Guid.NewGuid());
        Assert.Equal(3, (await Send(provider, reassign)).Version);
        var complete = new ActionChangeCommand(actionId, 3, "complete-1", ActionChangeKind.Complete);
        var changed = await Send(provider, complete);
        Assert.Equal("Completed", changed.State);
        Assert.Equal(4, changed.Version);
        Assert.Equal(changed, await Send(provider, complete));
        await Assert.ThrowsAsync<ActionWriteConflictException>(() => Send(provider,
            complete with { IdempotencyKey = "stale", ExpectedVersion = 1,
                Kind = ActionChangeKind.ChangeDueDate,
                NewDueLocalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(4)) }));
        Assert.Equal(1, await Count<FollowUpAction>(organisation));
        Assert.Equal(4, await Count<ClientManagement.Core.Entities.ActionChange>(organisation));
        Assert.Equal(4, await Count<IdempotencyReceipt>(organisation));
    }

    [Fact]
    public async Task Failed_receipt_insert_rolls_back_interaction_and_two_connection_parent_lock_serializes_support()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var seed = await Seed(organisation, actor);
        await using var providerA = Provider(organisation, actor);
        await using var providerB = Provider(organisation, actor);
        await database.ExecuteAdministratorAsync("""
            CREATE FUNCTION public.issue76_fail_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic receipt rollback'; END $$;
            CREATE TRIGGER issue76_fail_receipt BEFORE INSERT ON public."IdempotencyReceipts"
              FOR EACH ROW EXECUTE FUNCTION public.issue76_fail_receipt();
            """);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Send(providerA,
                new RecordInteractionCommand(seed.ClientId, seed.CaseId, DateTime.UtcNow,
                    "Synthetic rollback", "rollback-1")));
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                DROP TRIGGER issue76_fail_receipt ON public."IdempotencyReceipts";
                DROP FUNCTION public.issue76_fail_receipt();
                """);
        }
        Assert.Equal(0, await Count<Interaction>(organisation));
        Assert.Equal(0, await Count<IdempotencyReceipt>(organisation));

        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = Task.Run(async () =>
        {
            await using var scope = providerA.CreateAsyncScope();
            await Establish(scope.ServiceProvider);
            await scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>()
                .ExecuteOrdinaryOnceAsync(async token =>
                {
                    await scope.ServiceProvider.GetRequiredService<IClientParentLock>()
                        .LockAsync(seed.ClientId, token);
                    locked.SetResult();
                    await release.Task.WaitAsync(token);
                    return true;
                }, default);
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var support = Task.Run(() => Send(providerB, new UpsertSupportCommand
        {
            SupportId = seed.CaseId, ClientId = seed.ClientId, SocialWorkerId = seed.WorkerId,
            StartDate = DateTime.Today.AddDays(-2), Note = "serialized-support"
        }));
        try
        {
            await Task.Delay(150);
            Assert.False(support.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await hold.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(seed.CaseId, await support.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("close")]
    [InlineData("delete")]
    [InlineData("restore")]
    public async Task Every_other_support_writer_waits_for_same_client_parent_lock(string operation)
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var seed = await Seed(organisation, actor);
        if (operation == "restore") await SetCaseSoftDelete(organisation, seed.CaseId, true);
        await using var writer = Provider(organisation, actor);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // No organisation advisory fence in the holder: this isolates the Client row lock.
        var hold = HoldRowLock(organisation, "Clients", "Id", seed.ClientId, locked, release);
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = Task.Run(async () =>
        {
            started.SetResult();
            switch (operation)
            {
                case "create":
                    await Send(writer, new UpsertSupportCommand
                    {
                        ClientId = seed.ClientId, SocialWorkerId = seed.WorkerId,
                        StartDate = DateTime.Today.AddDays(1), Note = "next support"
                    });
                    break;
                case "close":
                    await Send(writer, new CloseSupportCommand
                    {
                        SupportId = seed.CaseId, EndDate = DateTime.Today.AddDays(1),
                        ReasonOfClosure = "Synthetic closure"
                    });
                    break;
                default:
                    await SendUnvalued(writer, new DeleteSupportCommand { Id = seed.CaseId });
                    break;
            }
        });
        await started.Task;
        try
        {
            await Task.Delay(150);
            Assert.False(attempt.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await hold.WaitAsync(TimeSpan.FromSeconds(5));
        await attempt.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Staff_projection_update_waits_for_current_worker_row_lock()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var seed = await Seed(organisation, actor);
        await using var projection = Provider(organisation, actor);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // No organisation advisory fence in the holder: the projection UPDATE must wait on worker.
        var hold = HoldRowLock(organisation, "SocialWorkers", "SocialWorkerId", seed.WorkerId,
            locked, release);
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = Task.Run(async () =>
        {
            await using var scope = projection.CreateAsyncScope();
            await Establish(scope.ServiceProvider);
            started.SetResult();
            await scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>()
                .ExecuteOrdinaryAsync(async token =>
                {
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var worker = await context.SocialWorkers.SingleAsync(x => x.Id == seed.WorkerId, token);
                    worker.ApplyProjection(actor, 2, Guid.NewGuid(), false, "Synthetic", "Worker",
                        worker.UserName, "Team", "T");
                    await context.SaveChangesAsync(token);
                }, default);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Delay(150);
            Assert.False(update.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await hold.WaitAsync(TimeSpan.FromSeconds(5));
        await update.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Delete_notification_is_published_only_after_successful_commit()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        var seed = await Seed(organisation, actor);
        await AddPreviousCase(organisation, seed);
        var probe = new NotificationProbe();
        await using var provider = Provider(organisation, actor, notificationProbe: probe);
        await database.ExecuteAdministratorAsync("""
            CREATE FUNCTION public.issue76_fail_deferred_delete() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic deferred rollback'; END $$;
            CREATE CONSTRAINT TRIGGER issue76_fail_deferred_delete
              AFTER UPDATE ON public."SocialCases" DEFERRABLE INITIALLY DEFERRED
              FOR EACH ROW EXECUTE FUNCTION public.issue76_fail_deferred_delete();
            """);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SendUnvalued(provider,
                new DeleteSupportCommand { Id = seed.CaseId }));
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                DROP TRIGGER issue76_fail_deferred_delete ON public."SocialCases";
                DROP FUNCTION public.issue76_fail_deferred_delete();
                """);
        }
        Assert.Equal(0, probe.Count);
        Assert.False(await CaseDeleted(organisation, seed.CaseId));
        await SendUnvalued(provider, new DeleteSupportCommand { Id = seed.CaseId });
        Assert.Equal(1, probe.Count);
        Assert.True(await CaseDeleted(organisation, seed.CaseId));
    }

    [Fact]
    public async Task Non_replaying_ordinary_attempt_does_not_retry_serialization_failure()
    {
        var organisation = Guid.NewGuid(); var actor = Guid.NewGuid();
        await using var provider = Provider(organisation, actor);
        await using var scope = provider.CreateAsyncScope();
        await Establish(scope.ServiceProvider);
        var attempts = 0;
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>()
                .ExecuteOrdinaryOnceAsync(async token =>
                {
                    attempts++;
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await using var command = new NpgsqlCommand(
                        "DO $$ BEGIN RAISE EXCEPTION 'synthetic retryable' USING ERRCODE = '40001'; END $$",
                        (NpgsqlConnection)context.Database.GetDbConnection(),
                        (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction());
                    await command.ExecuteNonQueryAsync(token);
                    return true;
                }, default));
        Assert.Equal(PostgresErrorCodes.SerializationFailure, failure.SqlState);
        Assert.Equal(1, attempts);
    }

    private async Task<(int ClientId, int CaseId, int WorkerId)> Seed(Guid organisation, Guid membership)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("organisation", organisation.ToString("D"));
            await context.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var seed = new ApplicationDbContext(options, TenantEnforcementTests.Scope(organisation));
        await seed.Database.UseTransactionAsync(transaction);
        var index = Interlocked.Increment(ref sequence);
        var client = ClientManagement.Tests.Common.SyntheticClient.Create(
            ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: index));
        client.ReferenceNumber = $"issue76-{Guid.NewGuid():N}";
        var worker = new SocialWorker();
        worker.ApplyProjection(membership, 1, Guid.NewGuid(), true, "Synthetic", "Worker",
            Guid.NewGuid().ToString("N"), "Team", "T");
        var socialCase = new SocialCase(client, DateTime.Today.AddDays(-5), worker, "legacy note");
        seed.Add(socialCase);
        await seed.SaveChangesAsync();
        await transaction.CommitAsync();
        return (client.Id, socialCase.Id, worker.Id);
    }

    private async Task SetCaseEndDate(Guid organisation, int caseId, DateTime endDate)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, organisation);
        await using var command = new NpgsqlCommand(
            "UPDATE public.\"SocialCases\" SET \"EndDate\" = @end WHERE \"SchoolRegistrationId\" = @id",
            connection, transaction);
        command.Parameters.AddWithValue("end", endDate);
        command.Parameters.AddWithValue("id", caseId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private async Task SetCaseSoftDelete(Guid organisation, int caseId, bool deleted)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, organisation);
        await using var command = new NpgsqlCommand(
            "UPDATE public.\"SocialCases\" SET \"Softdelete\" = @deleted WHERE \"SchoolRegistrationId\" = @id",
            connection, transaction);
        command.Parameters.AddWithValue("deleted", deleted);
        command.Parameters.AddWithValue("id", caseId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private async Task AddOverlappingCase(Guid organisation, (int ClientId, int CaseId, int WorkerId) existing)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, organisation);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var seed = new ApplicationDbContext(options, TenantEnforcementTests.Scope(organisation));
        await seed.Database.UseTransactionAsync(transaction);
        var client = await seed.Clients.SingleAsync(x => x.Id == existing.ClientId);
        var worker = await seed.SocialWorkers.SingleAsync(x => x.Id == existing.WorkerId);
        seed.SocialCases.Add(new SocialCase(client, DateTime.Today.AddDays(-2), worker, "overlap"));
        await seed.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task AddPreviousCase(Guid organisation, (int ClientId, int CaseId, int WorkerId) existing)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, organisation);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var seed = new ApplicationDbContext(options, TenantEnforcementTests.Scope(organisation));
        await seed.Database.UseTransactionAsync(transaction);
        var client = await seed.Clients.SingleAsync(x => x.Id == existing.ClientId);
        var worker = await seed.SocialWorkers.SingleAsync(x => x.Id == existing.WorkerId);
        seed.SocialCases.Add(new SocialCase(client, DateTime.Today.AddDays(-15), worker, "earlier")
            { EndDate = DateTime.Today.AddDays(-10) });
        await seed.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<bool> CaseDeleted(Guid organisation, int caseId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.AdministratorConnectionString).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(organisation));
        return (await context.SocialCases.SingleAsync(x => x.Id == caseId)).Softdelete;
    }

    private static async Task SetTenant(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organisation)
    {
        await using var context = new NpgsqlCommand(
            "SELECT set_config('zeka.organisation_id', @organisation, true)", connection, transaction);
        context.Parameters.AddWithValue("organisation", organisation.ToString("D"));
        await context.ExecuteNonQueryAsync();
    }

    private async Task HoldRowLock(Guid organisation, string table, string idColumn, int id,
        TaskCompletionSource locked, TaskCompletionSource release)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, organisation);
        await using (var command = new NpgsqlCommand(
            $"SELECT 1 FROM public.\"{table}\" WHERE \"OrganisationId\" = @organisation AND \"{idColumn}\" = @id FOR UPDATE",
            connection, transaction))
        {
            command.Parameters.AddWithValue("organisation", organisation);
            command.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        locked.SetResult();
        await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transaction.CommitAsync();
    }

    private ServiceProvider Provider(Guid organisation, Guid membership, bool syntheticActionGate = false,
        bool all = true, NotificationProbe? notificationProbe = null)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["FileServerPath"] = Path.GetTempPath()
        });
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTenantContext();
        services.AddSingleton<ICurrentUserService, SyntheticCurrentUser>();
        services.AddScoped<IOperationIdentity>(_ => new Identity(organisation));
        services.AddApplication();
        ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        services.RemoveAll<IHostedService>();
        services.RemoveAll<ICurrentTenantAccess>();
        services.AddScoped<ICurrentTenantAccess>(_ => new Access(organisation, membership, all));
        if (syntheticActionGate)
        {
            services.RemoveAll<IActionWriteActivationGate>();
            services.AddSingleton<IActionWriteActivationGate, SyntheticActionGate>();
        }
        if (notificationProbe is not null)
        {
            services.AddSingleton(notificationProbe);
            services.AddSingleton<INotificationHandler<SendSocialWorkerChangedNotificationCommand>>(
                notificationProbe);
        }
        return services.BuildServiceProvider();
    }

    private static async Task<T> Send<T>(ServiceProvider provider, IRequest<T> request)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private static async Task SendUnvalued(ServiceProvider provider, IRequest request)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private static Task Establish(IServiceProvider services) =>
        services.GetRequiredService<TenantOperation>().AuthorizeAsync(
            new RequiresTenantPermissionAttribute("Clients.EditAll"), default);

    private async Task<int> Count<T>(Guid organisation) where T : class
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.AdministratorConnectionString).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(organisation));
        return await context.Set<T>().CountAsync();
    }

    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity
    {
        public string SubjectId => "issue76-synthetic-actor";
    }

    private sealed class SyntheticCurrentUser : ICurrentUserService
    {
        public string UserId => "issue76-synthetic-actor";
        public string Username => "synthetic";
    }

    private sealed class Access(Guid organisation, Guid membership, bool all) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid selected,
            CancellationToken cancellationToken) => Task.FromResult(new TenantAccessDecision(
                selected == organisation ? TenantAccessOutcome.Authorized : TenantAccessOutcome.Denied,
                selected == organisation ? new AuthorizedTenantMembership(organisation, membership,
                    all ? ["Clients.EditAll", "Clients.EditAssigned", "Clients.Delete"]
                        : ["Clients.EditAssigned", "Clients.Delete"],
                    "synthetic", DateTimeOffset.UtcNow) : null));
    }

    private sealed class SyntheticActionGate : IActionWriteActivationGate
    {
        public void DemandActionWrite() { }
    }

    private sealed class ReconciledReceiptStore(RecordInteractionResult result) : IActionWriteStore
    {
        public Task<RecordInteractionResult> RecordAsync(RecordInteractionCommand request, Guid actor,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ActionChangeResult> ChangeAsync(ActionChangeCommand request, Guid actor,
            CancellationToken token) => throw new NotSupportedException();
        public Task<RecordInteractionResult?> ReconcileAsync(RecordInteractionCommand request, Guid actor,
            CancellationToken token) => Task.FromResult<RecordInteractionResult?>(result);
        public Task<ActionChangeResult?> ReconcileAsync(ActionChangeCommand request, Guid actor,
            CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class UncertainOnceExecutor : ITenantTransactionExecutor
    {
        public int Attempts { get; private set; }
        public async Task<T> ExecuteOrdinaryOnceAsync<T>(Func<CancellationToken, Task<T>> work,
            CancellationToken token)
        {
            Attempts++;
            var result = await work(token);
            if (Attempts == 1) throw new TenantCommitIndeterminateException(new Exception("synthetic"));
            return result;
        }
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            throw new NotSupportedException();
        public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<T> ExecuteOrdinaryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            throw new NotSupportedException();
        public Task ExecuteOrdinaryAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<T> ExecuteLifecycleAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            throw new NotSupportedException();
        public Task ExecuteLifecycleAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class NotificationProbe : INotificationHandler<SendSocialWorkerChangedNotificationCommand>
    {
        public int Count { get; private set; }
        public Task Handle(SendSocialWorkerChangedNotificationCommand notification,
            CancellationToken cancellationToken)
        { Count++; return Task.CompletedTask; }
    }
}

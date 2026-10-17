using ClientManagement.Application.Clients.Queries.LookupBeneficiaries;
using ClientManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed partial class DossierLookupHttpTests
{
    [Fact]
    public async Task Lookup_cancellation_terminates_the_in_flight_postgresql_query_without_a_result()
    {
        var access = new Access(assignedOnly: false);
        var id = await Seed(access.Organisation, access.Membership);
        await using var runtime = new NpgsqlConnection(database.RuntimeConnectionString);
        await runtime.OpenAsync();
        await using var runtimeTransaction = await runtime.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('zeka.organisation_id', @id, true)", runtime, runtimeTransaction))
        {
            scope.Parameters.AddWithValue("id", access.Organisation.ToString("D"));
            await scope.ExecuteNonQueryAsync();
        }
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(runtime).Options,
            TenantEnforcementTests.Scope(access.Organisation));
        await context.Database.UseTransactionAsync(runtimeTransaction);
        var handler = new LookupBeneficiariesQueryHandler(new RepositoryManager(context));

        await using var blocker = new NpgsqlConnection(database.AdministratorConnectionString);
        await blocker.OpenAsync();
        await using var blockingTransaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "LOCK TABLE public.\"Clients\" IN ACCESS EXCLUSIVE MODE", blocker, blockingTransaction))
            await command.ExecuteNonQueryAsync();
        // A separate autocommit observer avoids PostgreSQL transaction-level activity snapshots.
        await using var observer = new NpgsqlConnection(database.AdministratorConnectionString);
        await observer.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        // Also permit the former synchronous handler to reach PostgreSQL without blocking this test.
        var lookup = Task.Run(() => handler.Handle(
            new LookupBeneficiariesQuery { SearchText = "Synthetic" }, cancellation.Token));
        try
        {
            await ObserveLookupState(observer, runtime.ProcessID, blocker.ProcessID, waiting: true);
            Assert.False(lookup.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await lookup.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(lookup.IsCanceled); // A successful lookup DTO must never escape cancellation.
            // Prove server-side termination before releasing the lock that prevented completion.
            await ObserveLookupState(observer, runtime.ProcessID, blocker.ProcessID, waiting: false);
            Assert.NotNull(blockingTransaction.Connection);
        }
        finally
        {
            cancellation.Cancel();
            await blockingTransaction.RollbackAsync();
            // Release even on assertion failure so the old implementation cannot strand the fixture.
            try { await lookup.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        await runtimeTransaction.RollbackAsync();

        // The same endpoint and restricted PostgreSQL runtime still return legitimate results.
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        var success = await Lookup(client, "Synthetic");
        Assert.Equal(id, Assert.Single(success.Items).Id);
        Assert.False(success.HasMore);
    }

    private static async Task ObserveLookupState(NpgsqlConnection observer, int lookupPid, int blockerPid, bool waiting)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT CASE WHEN @waiting
                  THEN state = 'active' AND wait_event_type = 'Lock'
                    AND @blocker = ANY(pg_blocking_pids(pid))
                  ELSE state <> 'active' AND wait_event_type IS DISTINCT FROM 'Lock'
                END
                FROM pg_stat_activity WHERE pid = @lookup
                """, observer);
            command.Parameters.AddWithValue("lookup", lookupPid);
            command.Parameters.AddWithValue("blocker", blockerPid);
            command.Parameters.AddWithValue("waiting", waiting);
            if (await command.ExecuteScalarAsync(deadline.Token) is true) return;
            // This is only polling cadence; the PostgreSQL predicate is the synchronization proof.
            await Task.Delay(20, deadline.Token);
        }
    }
}

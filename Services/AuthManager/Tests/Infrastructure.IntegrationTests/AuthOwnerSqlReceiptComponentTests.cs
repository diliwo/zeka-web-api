using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Workers;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

// This is ordinary PG17 component coverage using the existing fixture's admin
// connection. It does not admit a restricted-role/RLS or ZEK-37 runtime witness.
[Collection(AuthOwnerSqlReceiptCollection.Name)]
public sealed class AuthOwnerSqlReceiptComponentTests(WorkerRotationPostgreSqlFixture fixture, ITestOutputHelper output)
    : IClassFixture<WorkerRotationPostgreSqlFixture>
{
    private const string ProducerFile = "Services/AuthManager/AuthManager.Infrastructure/Workers/WorkerIdentitySource.cs";
    private static readonly int[] OwnerReadLines = [20, 23, 26, 29, 32];

    [Fact]
    public async Task PublicOwnerValidationWritesFiveSourceBoundSuccessfulReadReceipts()
    {
        Assert.NotEqual("1", Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var connection = await fixture.CreateDatabaseAsync();
        var seed = await SeedOwnerAsync(connection, deadline.Token);
        using var evidence = new ReceiptEnvironment();
        await using var services = Services(connection);
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(deadline.Token);
        using var dispatches = new AuthOwnerSqlReceiptTests.PhysicalDispatchProbe();

        await dispatches.ObserveAsync(() => scope.ServiceProvider.GetRequiredService<WorkerIdentitySource>()
            .ValidateOwnerAsync(seed.Caller, seed.Organisation, deadline.Token));

        Assert.Equal(10, dispatches.Count); // Five locking reads and five current-authority reads.
        Assert.Same(transaction, database.Database.CurrentTransaction);
        AssertReceipts(evidence, OwnerReadLines, "success", null);
        await transaction.RollbackAsync(deadline.Token);
        Assert.Null(database.Database.CurrentTransaction);
        output.WriteLine("PG17 component; container {0}; server {1}; five genuine completed read receipts; transaction rolled back.",
            fixture.ContainerId, fixture.ServerVersion);
    }

    [Fact]
    public async Task PublicOwnerValidationPreservesRealLockFaultAndItsSingleTerminalReceipt()
    {
        Assert.NotEqual("1", Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var connection = await fixture.CreateDatabaseAsync();
        var seed = await SeedOwnerAsync(connection, deadline.Token);
        // Construct and lock with evidence disabled. The blocker uses only the
        // same existing public operation; no new SQL, privilege or schema setup.
        await using var blockerServices = Services(connection);
        await using var blockerScope = blockerServices.CreateAsyncScope();
        var blocker = blockerScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var held = await blocker.Database.BeginTransactionAsync(deadline.Token);
        await blockerScope.ServiceProvider.GetRequiredService<WorkerIdentitySource>()
            .ValidateOwnerAsync(seed.Caller, seed.Organisation, deadline.Token);

        using var evidence = new ReceiptEnvironment();
        var bounded = new NpgsqlConnectionStringBuilder(connection) { Options = "-c lock_timeout=1500" };
        await using var services = Services(bounded.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.False(database.Database.CreateExecutionStrategy().RetriesOnFailure);
        await using var transaction = await database.Database.BeginTransactionAsync(deadline.Token);
        using var dispatches = new AuthOwnerSqlReceiptTests.PhysicalDispatchProbe();
        Exception? original = null;

        await dispatches.ObserveAsync(async () => original = await Record.ExceptionAsync(() =>
            scope.ServiceProvider.GetRequiredService<WorkerIdentitySource>()
                .ValidateOwnerAsync(seed.Caller, seed.Organisation, deadline.Token)));

        var fault = original as PostgresException
            ?? Assert.IsType<PostgresException>(Assert.IsType<InvalidOperationException>(original).InnerException);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, fault.SqlState);
        Assert.False(fault.Data.Contains("OwnerSqlEvidenceCaptureFailed"));
        Assert.Equal(1, dispatches.Count);
        AssertReceipts(evidence, [20], "postgres_error", PostgresErrorCodes.LockNotAvailable);
        await transaction.RollbackAsync(deadline.Token);
        await held.RollbackAsync(deadline.Token);
        Assert.Null(database.Database.CurrentTransaction);
        Assert.Null(blocker.Database.CurrentTransaction);
        output.WriteLine("PG17 component; container {0}; server {1}; original SQLSTATE55P03; one physical dispatch/terminal receipt; both transactions rolled back.",
            fixture.ContainerId, fixture.ServerVersion);
    }

    private static ServiceProvider Services(string connection)
    {
        var safe = new NpgsqlConnectionStringBuilder(connection) { IncludeErrorDetail = false, LogParameters = false };
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddDbContext<AuthDbContext>(options => options
            .UseNpgsql(safe.ConnectionString, provider => provider.CommandTimeout(12))
            .EnableSensitiveDataLogging(false));
        services.AddIdentityCore<User>().AddEntityFrameworkStores<AuthDbContext>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<WorkerIdentitySource>();
        return services.BuildServiceProvider();
    }

    private static async Task<OwnerSeed> SeedOwnerAsync(string connection, CancellationToken ct)
    {
        await using var services = Services(connection);
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var now = DateTimeOffset.UtcNow;
        var subject = Guid.NewGuid();
        var organisation = Guid.NewGuid();
        var caller = new WorkerRotationCaller(subject, Guid.NewGuid());
        var owner = User.Create("operator@example.invalid", "operator", "Synthetic", "Owner", now);
        owner.Id = subject;
        owner.EmailConfirmed = true;
        owner.ActivateConfirmedIdentity(now);
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(owner)).Succeeded);
        var org = Organisation.Create(organisation, "Synthetic receipt component", subject, now);
        Assert.True(org.Activate(now));
        database.Organisations.Add(org);
        database.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation, subject, now));
        database.BrowserSessionFamilies.Add(new BrowserSessionFamily
        {
            Id = caller.SessionId, UserId = subject, SecurityStamp = owner.SecurityStamp!,
            CreatedAtUtc = now, IdleExpiresAtUtc = now.AddHours(1), AbsoluteExpiresAtUtc = now.AddHours(1)
        });
        await database.SaveChangesAsync(ct);
        return new(caller, organisation);
    }

    private static void AssertReceipts(ReceiptEnvironment evidence, int[] expectedLines, string outcome, string? state)
    {
        var rows = Directory.EnumerateFiles(evidence.Output, "owner-sql-*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(expectedLines.Select(line => ProducerFile + ":L" + line).OrderBy(value => value),
                rows.Select(row => row.RootElement.GetProperty("producerId").GetString()).OrderBy(value => value));
            var producerHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(evidence.Source, ProducerFile)))).ToLowerInvariant();
            string[] fields = ["schema", "producerId", "producerSha256", "querySha256", "serverMajor", "executionCompleted",
                "outcome", "sqlState", "processId", "observedAtUtc"];
            foreach (var document in rows)
            {
                var row = document.RootElement;
                Assert.Equal(fields.OrderBy(value => value), row.EnumerateObject().Select(field => field.Name).OrderBy(value => value));
                Assert.Equal("zeka-owner-sql-execution/v1", row.GetProperty("schema").GetString());
                Assert.Equal(producerHash, row.GetProperty("producerSha256").GetString());
                Assert.Matches("^[a-f0-9]{64}$", row.GetProperty("querySha256").GetString()!);
                Assert.Equal(17, row.GetProperty("serverMajor").GetInt32());
                Assert.True(row.GetProperty("executionCompleted").GetBoolean());
                Assert.Equal(outcome, row.GetProperty("outcome").GetString());
                Assert.Equal(state, row.GetProperty("sqlState").GetString());
                Assert.Equal(Environment.ProcessId, row.GetProperty("processId").GetInt32());
                Assert.Equal(TimeSpan.Zero, row.GetProperty("observedAtUtc").GetDateTimeOffset().Offset);
            }
        }
        finally { foreach (var document in rows) document.Dispose(); }
    }

    private sealed record OwnerSeed(WorkerRotationCaller Caller, Guid Organisation);

    private sealed class ReceiptEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);
        public string Source { get; }
        public string Output { get; } = Directory.CreateTempSubdirectory("auth-receipt-component-").FullName;

        public ReceiptEnvironment([CallerFilePath] string caller = "")
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(caller)!);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ProducerFile))) directory = directory.Parent;
            Assert.NotNull(directory);
            Source = directory!.FullName;
            Set("ZEKA_OWNER_SQL_SOURCE_ROOT", Source);
            Set("ZEKA_OWNER_SQL_EVIDENCE_DIR", Output);
            Set("ZEKA_OWNER_SQL_EVIDENCE", "1");
        }

        private void Set(string key, string value)
        {
            previous.Add(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, value) in previous.Reverse()) Environment.SetEnvironmentVariable(key, value);
            // Retain the public-only receipts for readback; no resource-wide cleanup.
        }
    }
}

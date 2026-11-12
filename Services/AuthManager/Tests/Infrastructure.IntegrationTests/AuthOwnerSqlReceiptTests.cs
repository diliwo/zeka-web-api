using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthManager.Application.Workers;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Infrastructure.IntegrationTests;

// Receipt settings are process-wide. Run these cases without concurrent test collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthOwnerSqlReceiptCollection
{
    public const string Name = "Auth owner SQL receipt";
}

[Collection(AuthOwnerSqlReceiptCollection.Name)]
public sealed class AuthOwnerSqlReceiptTests
{
    private const string FixtureRoot = "/tmp/zek37-probe";
    private const string Socket = "/var/run/postgresql";
    private const string Role = "zeka_auth_runtime";
    private const string V2 = "zeka-owner-sql-execution/v2";
    private const string Producer =
        "Services/AuthManager/AuthManager.Infrastructure/Workers/WorkerIdentitySource.cs:L20";

    [Fact]
    public async Task CaptureRejectsUnattributedFailuresAndPredispatchCancellation()
    {
        using var evidence = EvidenceScope.Temporary();
        await using var database = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().Options);
        var fabricated = new PostgresException("synthetic test fault", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable);
        var errors = new Exception[]
        {
            new InvalidOperationException("unrelated test fault"),
            fabricated,
            new InvalidOperationException("unmatched test wrapper", fabricated)
        };
        foreach (var error in errors)
            Assert.Same(error, await Record.ExceptionAsync(() => ObserveLoad(database, () => Task.FromException(error))));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await Record.ExceptionAsync(() =>
            ObserveLoad(database, () => Task.FromCanceled(cancelled.Token)));
        Assert.Equal(cancelled.Token, Assert.IsAssignableFrom<OperationCanceledException>(cancellation).CancellationToken);
        Assert.Empty(evidence.ReadRows());
    }

    [Fact]
    public async Task CaptureRejectsReentrantObservationWithoutInventingReceipt()
    {
        using var evidence = EvidenceScope.Temporary();
        await using var database = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().Options);
        var reentered = false;
        var error = await Record.ExceptionAsync(() => ObserveLoad(database, () =>
            ObserveLoad(database, () =>
            {
                reentered = true;
                return Task.CompletedTask;
            })));
        Assert.Equal("OWNER_SQL_EF_NESTED_CAPTURE_REJECTED", Assert.IsType<InvalidOperationException>(error).Message);
        Assert.False(reentered);
        Assert.Empty(evidence.ReadRows());
    }

    internal static async Task RunAdmittedProviderMatrixAsync(CancellationToken cancellationToken)
    {
        var stage = "FIXTURE_ADMISSION";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var fixture = FixtureAdmission.Read();
            await fixture.VerifyAsync(deadline.Token);
            foreach (var test in Enum.GetValues<ProviderCase>())
            {
                stage = test.ToString().ToUpperInvariant();
                await VerifyProviderCaseAsync(fixture, test, deadline.Token);
            }
        }
        catch (Exception)
        {
            // Test output carries the bounded stage, never provider exception text or data.
            throw new InvalidOperationException("AUTH_OWNER_SQL_MATRIX_STOP_" + stage);
        }
    }

    private static async Task VerifyProviderCaseAsync(FixtureAdmission fixture, ProviderCase test, CancellationToken ct)
    {
        using var evidence = EvidenceScope.ForFixture(test);
        using var dispatches = new PhysicalDispatchProbe();
        var commands = new CommandProbe(test, evidence.DirectoryPath);
        var transactions = new TransactionProbe();
        var wraps = test is ProviderCase.Wrapped or ProviderCase.Collector;
        var blocks = test != ProviderCase.Partial;
        var connectionString = fixture.ConnectionString(test is ProviderCase.Cancel or ProviderCase.Partial ? 0 : 1500);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddDbContext<AuthDbContext>(options => options
            .UseNpgsql(connectionString, provider =>
            {
                if (!wraps)
                    provider.ExecutionStrategy(dependencies => new NonRetryingExecutionStrategy(dependencies));
            })
            .EnableSensitiveDataLogging(false)
            .AddInterceptors(commands, transactions));
        services.AddIdentityCore<User>().AddEntityFrameworkStores<AuthDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var strategy = database.Database.CreateExecutionStrategy();
        Need(!strategy.RetriesOnFailure && (wraps
            ? strategy.GetType().Name == "NpgsqlExecutionStrategy"
            : strategy is NonRetryingExecutionStrategy));
        await database.Database.OpenConnectionAsync(ct);
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        await fixture.VerifyConnectionAsync(connection, ct);

        await using var blocker = new NpgsqlConnection(fixture.ConnectionString());
        await blocker.OpenAsync(ct);
        await using var blockTransaction = await blocker.BeginTransactionAsync(ct);
        await SetTenantAsync(blocker, blockTransaction, fixture.Organisation, ct);
        if (blocks)
        {
            await using var hold = blocker.CreateCommand();
            hold.Transaction = blockTransaction;
            hold.CommandText = """SELECT 1 FROM public."Organisations" WHERE "Id" = @id FOR UPDATE""";
            hold.Parameters.AddWithValue("id", fixture.Organisation);
            Need(await hold.ExecuteScalarAsync(ct) is int count && count == 1);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var nativeTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        await SetTenantAsync(connection, nativeTransaction, fixture.Organisation, ct);
        var advisoryKey = Random.Shared.NextInt64();
        if (test == ProviderCase.Cancel)
        {
            Need(await TryAdvisoryLockAsync(connection, nativeTransaction, advisoryKey, ct));
            Need(!await TryAdvisoryLockAsync(blocker, blockTransaction, advisoryKey, ct));
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var identities = new WorkerIdentitySource(database, users, TimeProvider.System);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? observed = null;
        var operation = dispatches.ObserveAsync(async () =>
        {
            try
            {
                await identities.ValidateOwnerAsync(new(Guid.NewGuid(), Guid.NewGuid()), fixture.Organisation, cancel.Token);
            }
            catch (Exception error)
            {
                observed = error;
            }
        });
        if (test == ProviderCase.Cancel)
        {
            try
            {
                await WaitForBlockAsync(blocker, blockTransaction, connection.ProcessID, operation, ct);
            }
            catch
            {
                cancel.Cancel();
                await operation.WaitAsync(TimeSpan.FromSeconds(12));
                throw;
            }
            cancel.Cancel();
        }

        await operation.WaitAsync(ct);
        Need(dispatches.Count == 1 && commands.CommandStarts == 1 && transactions.Starts == 1);

        if (test == ProviderCase.Cancel)
        {
            Need(observed is OperationCanceledException cancelled && cancelled.CancellationToken == cancel.Token
                && cancelled.InnerException is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled }
                && commands.Cancelled == 1);
            AssertTerminal(evidence.ReadRows(), "cancelled", false, null, V2);
        }
        else if (test == ProviderCase.Partial)
        {
            Need(observed is PartialReaderAbort && commands.ReadFirstRow && commands.DisposedReader);
            Need(evidence.ReadRows().Length == 0);
        }
        else
        {
            var fault = commands.Fault as PostgresException ?? throw new ReceiptAssertion();
            Need(fault.SqlState == PostgresErrorCodes.LockNotAvailable);
            Need(wraps
                ? observed?.GetType() == typeof(InvalidOperationException)
                    && ReferenceEquals(observed.InnerException, fault)
                    && observed.StackTrace?.Contains("NpgsqlExecutionStrategy", StringComparison.Ordinal) == true
                : ReferenceEquals(observed, fault));
            Need(observed?.StackTrace?.Contains(nameof(WorkerIdentitySource.ValidateOwnerAsync), StringComparison.Ordinal) == true);

            switch (test)
            {
                case ProviderCase.Mismatch:
                    Need(commands.InjectedCallbacks == 1 && evidence.ReadRows().Length == 0);
                    break;
                case ProviderCase.Conflict:
                    Need(commands.InjectedCallbacks == 2 && evidence.ReadRows().Length == 0);
                    break;
                case ProviderCase.Collector:
                    Need(fault.Data.Contains("OwnerSqlEvidenceCaptureFailed")
                        && !Directory.Exists(evidence.DirectoryPath));
                    break;
                default:
                    Need(commands.InjectedCallbacks == (test == ProviderCase.Duplicate ? 1 : 0));
                    AssertTerminal(evidence.ReadRows(), "postgres_error", true, PostgresErrorCodes.LockNotAvailable);
                    break;
            }
        }

        await transaction.RollbackAsync(ct);
        Need(transactions.Rollbacks == 1 && database.Database.CurrentTransaction is null);
        if (test == ProviderCase.Cancel)
            Need(await TryAdvisoryLockAsync(blocker, blockTransaction, advisoryKey, ct));
        if (test == ProviderCase.Partial)
        {
            await using (var recovered = connection.CreateCommand())
            {
                recovered.CommandText = "SELECT 1";
                Need(await recovered.ExecuteScalarAsync(ct) is int value && value == 1);
            }
            await database.Database.CloseConnectionAsync();
            Need(connection.State == ConnectionState.Closed);
        }
        await blockTransaction.RollbackAsync(ct);
        Console.WriteLine($"MATRIX={test.ToString().ToUpperInvariant()}|PHYSICAL=1|EF_COMMAND_START=1|TX_START=1|TX_ROLLBACK=1");
    }

    private static void AssertTerminal(JsonElement[] rows, string outcome, bool complete, string? sqlState, string? schema = null)
    {
        Need(rows.Length == 1);
        var row = rows[0];
        Need(row.GetProperty("producerId").GetString() == Producer
            && row.GetProperty("serverMajor").GetInt32() == 17
            && row.GetProperty("outcome").GetString() == outcome
            && row.GetProperty("executionCompleted").GetBoolean() == complete
            && row.GetProperty("sqlState").GetString() == sqlState
            && (schema is null || row.GetProperty("schema").GetString() == schema));
    }

    private static async Task WaitForBlockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int backendPid, Task operation, CancellationToken ct)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        observation.CancelAfter(TimeSpan.FromSeconds(8));
        while (!operation.IsCompleted)
        {
            await using var wait = connection.CreateCommand();
            wait.Transaction = transaction;
            wait.CommandText = "SELECT cardinality(pg_catalog.pg_blocking_pids(@pid)) > 0";
            wait.Parameters.AddWithValue("pid", backendPid);
            if (await wait.ExecuteScalarAsync(observation.Token) is true)
                return;
            await Task.Delay(25, observation.Token);
        }
        throw new ReceiptAssertion();
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid organisation, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.set_config('zeka.organisation_id', @id, true)";
        command.Parameters.AddWithValue("id", organisation.ToString("D"));
        Need(Equals(await command.ExecuteScalarAsync(ct), organisation.ToString("D")));
    }

    private static async Task<bool> TryAdvisoryLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.pg_try_advisory_xact_lock(@key)";
        command.Parameters.AddWithValue("key", key);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static DbCommandInterceptor Observer(AuthDbContext database) =>
        (DbCommandInterceptor)(typeof(AuthDbContext).GetProperty("OwnerSqlReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(database) ?? throw new ReceiptAssertion());

    private static Task ObserveLoad(AuthDbContext database, Func<Task> operation) =>
        (Task)Observer(database).GetType().GetMethod("ObserveAuthLoadAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [database, operation, "", 0])!;

    private static void Need(bool condition)
    {
        if (!condition)
            throw new ReceiptAssertion();
    }

    private enum ProviderCase { Direct, Wrapped, Duplicate, Mismatch, Conflict, Collector, Cancel, Partial }

    private sealed class ReceiptAssertion : Exception;
    private sealed class PartialReaderAbort : Exception;

    private sealed class CommandProbe(ProviderCase test, string evidenceDirectory) : DbCommandInterceptor
    {
        public Exception? Fault { get; private set; }
        public int CommandStarts { get; private set; }
        public int InjectedCallbacks { get; private set; }
        public int Cancelled { get; private set; }
        public bool ReadFirstRow { get; private set; }
        public bool DisposedReader { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Need(data.CommandSource == CommandSource.FromSqlQuery && !data.LogParameterValues && !result.HasResult);
            Need(++CommandStarts == 1);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Need(test == ProviderCase.Partial && result is NpgsqlDataReader);
            ReadFirstRow = await result.ReadAsync(cancellationToken);
            await result.DisposeAsync();
            DisposedReader = result.IsClosed;
            throw new PartialReaderAbort();
        }

        public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData data,
            CancellationToken cancellationToken = default)
        {
            Need(test == ProviderCase.Cancel && data.CommandSource == CommandSource.FromSqlQuery);
            Need(++Cancelled == 1);
            return Task.CompletedTask;
        }

        public override async Task CommandFailedAsync(DbCommand command, CommandErrorEventData data,
            CancellationToken cancellationToken = default)
        {
            // The early reader abort is deliberately a test fault, not a PostgreSQL fault.
            if (test == ProviderCase.Partial && data.Exception is PartialReaderAbort)
                return;

            Need(data.CommandSource == CommandSource.FromSqlQuery && data.Context is AuthDbContext);
            Need(Fault is null);
            Fault = data.Exception;
            if (test == ProviderCase.Collector)
                Directory.Delete(evidenceDirectory);
            if (test == ProviderCase.Duplicate)
            {
                await Observer((AuthDbContext)data.Context!).CommandFailedAsync(command, data, cancellationToken);
                InjectedCallbacks++;
            }
            if (test == ProviderCase.Mismatch)
            {
                await using var other = new NpgsqlCommand();
                await Observer((AuthDbContext)data.Context!).CommandFailedAsync(other, data, cancellationToken);
                InjectedCallbacks++;
                // Stop the interceptor chain before it reaches the genuine matching callback.
                ExceptionDispatchInfo.Capture(data.Exception).Throw();
            }
            if (test == ProviderCase.Conflict)
            {
                var observer = Observer((AuthDbContext)data.Context!);
                await observer.CommandFailedAsync(command, data, cancellationToken);
                var conflict = new PostgresException("synthetic conflict", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable);
                var definition = new EventDefinition(data.Context!.GetService<ILoggingOptions>(),
                    RelationalEventId.CommandError, LogLevel.Error, nameof(RelationalEventId.CommandError), _ => (_, _) => { });
                var conflictingData = new CommandErrorEventData(definition, (_, _) => "synthetic conflict",
                    data.Connection, command, data.Context, data.ExecuteMethod, data.CommandId, data.ConnectionId,
                    conflict, data.IsAsync, data.LogParameterValues, data.StartTime, data.Duration, data.CommandSource);
                await observer.CommandFailedAsync(command, conflictingData, cancellationToken);
                InjectedCallbacks += 2;
            }
        }
    }

    private sealed class TransactionProbe : DbTransactionInterceptor
    {
        public int Starts { get; private set; }
        public int Rollbacks { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Need(!result.HasResult && ++Starts == 1);
            return ValueTask.FromResult(result);
        }

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData data,
            CancellationToken cancellationToken = default)
        {
            Need(++Rollbacks == 1);
            return Task.CompletedTask;
        }
    }

    public sealed class PhysicalDispatchProbe : IDisposable
    {
        private readonly AsyncLocal<bool> active = new();
        private readonly ActivityListener listener;
        private int count;

        public PhysicalDispatchProbe()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.PropagationData,
                ActivityStarted = _ =>
                {
                    if (active.Value)
                        Interlocked.Increment(ref count);
                }
            };
            ActivitySource.AddActivityListener(listener);
        }

        public int Count => Volatile.Read(ref count);

        public async Task ObserveAsync(Func<Task> operation)
        {
            Need(!active.Value);
            active.Value = true;
            try { await operation(); }
            finally { active.Value = false; }
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed record FixtureAdmission(string Database, Guid Organisation)
    {
        public static FixtureAdmission Read()
        {
            var container = Environment.GetEnvironmentVariable("ZEKA37_FIXTURE_ID") ?? "";
            var database = Environment.GetEnvironmentVariable("ZEKA37_DB_NAME") ?? "";
            var system = Environment.GetEnvironmentVariable("ZEKA37_SYSTEM_ID") ?? "";
            Need(OperatingSystem.IsLinux() && Regex.IsMatch(container, "^[a-f0-9]{64}$")
                && Regex.IsMatch(database, "^zek37_pg17_[a-f0-9]{12}$")
                && Regex.IsMatch(system, "^[0-9]{16,20}$")
                && Environment.MachineName == container[..12] && Directory.Exists(Socket));
            Need(Directory.EnumerateDirectories("/sys/class/net").Select(Path.GetFileName).SequenceEqual(["lo"]));
            Need(Guid.TryParseExact(Environment.GetEnvironmentVariable("ZEKA37_TEST_ORG_ID"), "D", out var organisation)
                && organisation != Guid.Empty);
            var manifest = FixtureRoot + "/fixture-admission.json";
            Need(File.Exists(manifest) && (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) == 0);
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var json = document.RootElement;
            Need(json.GetProperty("containerId").GetString() == container
                && !string.IsNullOrWhiteSpace(json.GetProperty("engineId").GetString())
                && Regex.IsMatch(json.GetProperty("imageId").GetString() ?? "", "^sha256:[a-f0-9]{64}$")
                && json.GetProperty("database").GetString() == database
                && json.GetProperty("clusterSystemId").GetString() == system
                && json.GetProperty("networkMode").GetString() == "none"
                && json.GetProperty("publishedPorts").GetInt32() == 0
                && json.GetProperty("bindMounts").GetInt32() == 0
                && json.GetProperty("mounts").GetInt32() == 0);
            var tmpfs = json.GetProperty("tmpfs").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal);
            Need(tmpfs.SequenceEqual(["/tmp", "/var/lib/postgresql/data", Socket]));
            return new FixtureAdmission(database, organisation);
        }

        public string ConnectionString(int lockTimeout = 0)
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = Socket, Database = Database, Username = Role, Pooling = false,
                IncludeErrorDetail = false, LogParameters = false, Timeout = 5, CommandTimeout = 12,
                Options = lockTimeout == 0 ? "" : $"-c lock_timeout={lockTimeout}"
            };
            var parsed = new NpgsqlConnectionStringBuilder(builder.ConnectionString);
            Need(parsed.Host == Socket && parsed.Database == Database && parsed.Username == Role
                && string.IsNullOrEmpty(parsed.Password) && !parsed.IncludeErrorDetail && !parsed.LogParameters);
            return builder.ConnectionString;
        }

        public async Task VerifyAsync(CancellationToken ct)
        {
            await using var connection = new NpgsqlConnection(ConnectionString());
            await connection.OpenAsync(ct);
            await VerifyConnectionAsync(connection, ct);
        }

        public async Task VerifyConnectionAsync(NpgsqlConnection connection, CancellationToken ct)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT current_database(), current_user, session_user, current_setting('listen_addresses') = '',
                    inet_server_addr() IS NULL, NOT r.rolsuper AND NOT r.rolcreaterole AND NOT r.rolcreatedb
                    AND NOT r.rolreplication AND NOT r.rolbypassrls AND NOT EXISTS
                    (SELECT 1 FROM pg_catalog.pg_auth_members AS m WHERE m.member = r.oid)
                FROM pg_catalog.pg_roles AS r WHERE r.rolname = current_user
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            Need(connection.PostgreSqlVersion.Major == 17 && await reader.ReadAsync(ct)
                && reader.GetString(0) == Database && reader.GetString(1) == Role && reader.GetString(2) == Role
                && reader.GetBoolean(3) && reader.GetBoolean(4) && reader.GetBoolean(5)
                && !await reader.ReadAsync(ct));
        }
    }

    private sealed class EvidenceScope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new(StringComparer.Ordinal);
        private readonly bool temporary;

        private EvidenceScope(string path, bool temporary)
        {
            DirectoryPath = path;
            this.temporary = temporary;
            Set("ZEKA_OWNER_SQL_EVIDENCE_DIR", path);
            Set("ZEKA_OWNER_SQL_EVIDENCE", "1");
        }

        public string DirectoryPath { get; }

        public static EvidenceScope Temporary() =>
            new(Directory.CreateTempSubdirectory("auth-receipt-unit-").FullName, true);

        public static EvidenceScope ForFixture(ProviderCase test)
        {
            var source = Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_SOURCE_ROOT");
            Need(source == FixtureRoot + "/source" && Directory.Exists(source));
            var parent = FixtureRoot + "/receipts";
            Need(Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0);
            var path = parent + "/matrix-" + test.ToString().ToLowerInvariant();
            Need(!Directory.Exists(path) && !File.Exists(path));
            Directory.CreateDirectory(path);
            return new EvidenceScope(path, false);
        }

        public JsonElement[] ReadRows() => Directory.Exists(DirectoryPath)
            ? Directory.GetFiles(DirectoryPath, "owner-sql-*.jsonl")
                .SelectMany(File.ReadAllLines).Select(Parse).ToArray()
            : [];

        private static JsonElement Parse(string line)
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }

        private void Set(string key, string value)
        {
            previous.Add(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            if (temporary && Directory.Exists(DirectoryPath) && !Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
                Directory.Delete(DirectoryPath);
        }
    }
}

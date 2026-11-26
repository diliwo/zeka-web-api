extern alias DbMigrator;

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Xunit;
using OwnerSqlExecution = DbMigrator::Zeka.OwnerSqlEvidence.OwnerSqlExecution;

namespace Zeka.DbMigrate.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OwnerSqlAdapterCollection
{
    public const string Name = "Actual owner SQL adapter";
}

// These are command/reader contract tests, not PostgreSQL or Auth execution evidence.
// The alias selects the production migrator assembly, never a linked test copy.
[Collection(OwnerSqlAdapterCollection.Name)]
public sealed class OwnerSqlExecutionCoverageTests
{
    [Fact]
    public void Adapter_is_the_actual_production_module()
    {
        Assert.Equal("zeka-db-migrate", typeof(OwnerSqlExecution).Assembly.GetName().Name);
        Assert.NotSame(typeof(OwnerSqlExecutionCoverageTests).Assembly, typeof(OwnerSqlExecution).Assembly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("01")]
    public void Capture_is_opt_in_to_the_exact_enabled_value(string? configured)
    {
        using var scope = new EvidenceSetting(configured);
        Assert.False(OwnerSqlExecution.IsEnabled);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("scalar")]
    [InlineData("nonquery")]
    [InlineData("bootstrap")]
    public async Task Disabled_capture_preserves_command_results(string operation)
    {
        using var scope = new EvidenceSetting("0");
        using var command = new ContractCommand();
        var result = await Execute(operation, command, CancellationToken.None);
        if (operation == "reader")
        {
            Assert.Same(command.Reader, result);
            await ((DbDataReader)result!).DisposeAsync();
        }
        else Assert.Equal(operation == "scalar" ? 17 : 23, result);
        Assert.Equal(1, command.Dispatches);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("scalar")]
    [InlineData("nonquery")]
    [InlineData("bootstrap")]
    public async Task Disabled_capture_preserves_original_provider_failures(string operation)
    {
        using var scope = new EvidenceSetting("0");
        var original = ProviderFailure();
        using var command = new ContractCommand { Failure = original };
        var observed = await Assert.ThrowsAsync<PostgresException>(() => Execute(operation, command, CancellationToken.None));
        Assert.Same(original, observed);
        Assert.False(original.Data.Contains("OwnerSqlEvidenceCaptureFailed"));
        Assert.Equal(1, command.Dispatches);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("scalar")]
    [InlineData("nonquery")]
    [InlineData("bootstrap")]
    public async Task Cancellation_keeps_the_original_token_and_has_no_dispatch(string operation)
    {
        using var scope = new EvidenceSetting("0");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var command = new ContractCommand();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(operation, command, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, command.Dispatches);
    }

    [Theory]
    [InlineData("reader", false)]
    [InlineData("scalar", false)]
    [InlineData("nonquery", false)]
    [InlineData("bootstrap", false)]
    [InlineData("reader", true)]
    [InlineData("scalar", true)]
    [InlineData("nonquery", true)]
    [InlineData("bootstrap", true)]
    public async Task Enabled_capture_refuses_missing_or_closed_postgres_before_dispatch(string operation, bool closedPostgres)
    {
        using var scope = new EvidenceSetting("1");
        using var connection = closedPostgres ? new NpgsqlConnection() : null;
        using var command = new ContractCommand { Connection = connection };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(operation, command, CancellationToken.None));
        Assert.Equal("OWNER_SQL_EVIDENCE_REQUIRES_POSTGRESQL_17", error.Message);
        Assert.Equal(0, command.Dispatches);
    }

    [Fact]
    public void Auth_observation_cannot_succeed_when_capture_is_disabled()
    {
        using var scope = new EvidenceSetting("0");
        using var command = new ContractCommand();
        var error = Assert.Throws<InvalidOperationException>(() => OwnerSqlExecution.ObserveAuthCommand(command, "unused", 1));
        Assert.Equal("OWNER_SQL_EVIDENCE_CONFIGURATION_MISSING", error.Message);
        Assert.Equal(0, command.Dispatches);
    }

    [Fact]
    public void Auth_observation_also_requires_a_genuine_open_postgres_connection()
    {
        using var scope = new EvidenceSetting("1");
        using var command = new ContractCommand();
        var error = Assert.Throws<InvalidOperationException>(() => OwnerSqlExecution.ObserveAuthCommand(command, "unused", 1));
        Assert.Equal("OWNER_SQL_EVIDENCE_REQUIRES_POSTGRESQL_17", error.Message);
        Assert.Equal(0, command.Dispatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_result_stream_must_finish_before_one_completion(bool asynchronous)
    {
        using var raw = new ContractReader(2);
        var completed = 0;
        var failed = 0;
        await using var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, _ => failed++);
        do
        {
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(0, completed);
        } while (asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.Equal(1, completed);
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.Equal(1, completed);
        Assert.Equal(0, failed);
    }

    [Theory]
    [InlineData("read", false)]
    [InlineData("read", true)]
    [InlineData("next", false)]
    [InlineData("next", true)]
    public async Task Deferred_provider_failure_is_original_and_terminal_once(string boundary, bool asynchronous)
    {
        using var raw = new ContractReader(1) { FailureAt = boundary, Failure = ProviderFailure() };
        var completed = 0;
        var failures = new List<PostgresException>();
        await using var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, failures.Add);
        if (boundary == "read") Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = asynchronous
                ? await Assert.ThrowsAsync<PostgresException>(() => boundary == "read" ? reader.ReadAsync() : reader.NextResultAsync())
                : Assert.Throws<PostgresException>(() => boundary == "read" ? reader.Read() : reader.NextResult());
            Assert.Same(raw.Failure, error);
        }
        Assert.Same(raw.Failure, Assert.Single(failures));
        raw.FailureAt = "";
        Assert.False(await reader.NextResultAsync());
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task GetValue_failure_is_not_replaced_or_credited_as_completion()
    {
        using var raw = new ContractReader(1) { FailureAt = "value", Failure = ProviderFailure() };
        var completed = 0;
        await using (var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, _ => { }))
        {
            Assert.True(await reader.ReadAsync());
            Assert.Same(raw.Failure, Assert.Throws<PostgresException>(() => reader.GetValue(0)));
        }
        // GetValue is a delegated accessor. This does not claim an error receipt was emitted.
        Assert.Equal(0, completed);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("dispose")]
    [InlineData("dispose-async")]
    public async Task Cleanup_failure_cannot_create_success_or_repeat_failure(string boundary)
    {
        using var raw = new ContractReader(1) { FailureAt = boundary, Failure = ProviderFailure() };
        var completed = 0;
        var failures = new List<PostgresException>();
        var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, failures.Add);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            PostgresException error;
            if (boundary == "dispose-async") error = await Assert.ThrowsAsync<PostgresException>(() => reader.DisposeAsync().AsTask());
            else error = Assert.Throws<PostgresException>(() => { if (boundary == "close") reader.Close(); else reader.Dispose(); });
            Assert.Same(raw.Failure, error);
        }
        Assert.Same(raw.Failure, Assert.Single(failures));
        Assert.Equal(0, completed);
        raw.FailureAt = "";
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task Collector_failure_never_replaces_the_deferred_provider_error()
    {
        using var raw = new ContractReader(1) { FailureAt = "read", Failure = ProviderFailure() };
        var callbacks = 0;
        await using var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => throw new InvalidOperationException(), _ =>
        {
            callbacks++;
            throw new IOException("Synthetic collector refusal.");
        });
        Assert.True(await reader.ReadAsync());
        Assert.Same(raw.Failure, await Assert.ThrowsAsync<PostgresException>(() => reader.ReadAsync()));
        Assert.True((bool)raw.Failure!.Data["OwnerSqlEvidenceCaptureFailed"]!);
        Assert.Same(raw.Failure, await Assert.ThrowsAsync<PostgresException>(() => reader.ReadAsync()));
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task Completion_helper_preserves_cancellation_without_success()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var raw = new ContractReader(1);
        var completed = 0;
        var failed = 0;
        await using var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, _ => failed++);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnerSqlExecution.CompleteReaderAsync(reader, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, completed);
        Assert.Equal(0, failed);
    }

    [Fact]
    public async Task Completion_helper_drains_the_actual_remaining_results()
    {
        using var raw = new ContractReader(2);
        var completed = 0;
        await using var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, _ => { });
        Assert.True(await reader.ReadAsync());
        await OwnerSqlExecution.CompleteReaderAsync(reader, CancellationToken.None);
        Assert.Equal(1, completed);
        Assert.Equal(2, raw.ResultsFinished);
    }

    [Fact]
    public async Task Partial_disposal_does_not_invent_completion()
    {
        using var raw = new ContractReader(1);
        var completed = 0;
        var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, _ => { });
        Assert.True(await reader.ReadAsync());
        await reader.DisposeAsync();
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task Typed_values_nulls_and_metadata_are_preserved_without_advancing_results()
    {
        using var table = new DataTable();
        foreach (var (name, type) in new[]
        {
            ("boolean", typeof(bool)), ("byte", typeof(byte)), ("bytes", typeof(byte[])),
            ("text", typeof(string)), ("character", typeof(char)), ("date", typeof(DateTime)),
            ("decimal", typeof(decimal)), ("double", typeof(double)), ("single", typeof(float)),
            ("guid", typeof(Guid)), ("short", typeof(short)), ("integer", typeof(int)),
            ("long", typeof(long)), ("nullable", typeof(string)), ("characters", typeof(char[]))
        }) table.Columns.Add(name, type);
        var id = Guid.NewGuid();
        table.Rows.Add(true, (byte)7, new byte[] { 1, 2, 3 }, "hello", 'z', DateTime.UnixEpoch,
            1.25m, 2.5d, 3.5f, id, (short)12, 34, 56L, DBNull.Value, "hello".ToCharArray());
        using var raw = table.CreateDataReader();
        var completed = 0;
        using var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, _ => { });
        Assert.True(reader.Read());
        Assert.Equal(0, reader.Depth);
        Assert.Equal(15, reader.FieldCount);
        Assert.Equal(15, reader.VisibleFieldCount);
        Assert.True(reader.HasRows);
        Assert.False(reader.IsClosed);
        Assert.Equal(raw.RecordsAffected, reader.RecordsAffected);
        Assert.Equal(34, reader[11]);
        Assert.Equal(34, reader["integer"]);
        Assert.True(reader.GetBoolean(0));
        Assert.Equal((byte)7, reader.GetByte(1));
        var bytes = new byte[3];
        Assert.Equal(3, reader.GetBytes(2, 0, bytes, 0, bytes.Length));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        var characters = new char[5];
        Assert.Equal(5, reader.GetChars(14, 0, characters, 0, characters.Length));
        Assert.Equal("hello", new string(characters));
        Assert.Equal('z', reader.GetChar(4));
        Assert.Equal(nameof(Int32), reader.GetDataTypeName(11));
        Assert.Equal(DateTime.UnixEpoch, reader.GetDateTime(5));
        Assert.Equal(1.25m, reader.GetDecimal(6));
        Assert.Equal(2.5d, reader.GetDouble(7));
        Assert.Equal(3.5f, reader.GetFloat(8));
        Assert.Equal(typeof(int), reader.GetFieldType(11));
        Assert.Equal(id, reader.GetGuid(9));
        Assert.Equal((short)12, reader.GetInt16(10));
        Assert.Equal(34, reader.GetInt32(11));
        Assert.Equal(56L, reader.GetInt64(12));
        Assert.Equal("integer", reader.GetName(11));
        Assert.Equal(11, reader.GetOrdinal("integer"));
        Assert.Equal("hello", reader.GetString(3));
        var values = new object[15];
        Assert.Equal(15, reader.GetValues(values));
        Assert.Equal(34, values[11]);
        Assert.True(reader.IsDBNull(13));
        Assert.True(await reader.IsDBNullAsync(13));
        Assert.Equal(34, reader.GetFieldValue<int>(11));
        Assert.Equal(34, await reader.GetFieldValueAsync<int>(11));
        using var schema = reader.GetSchemaTable();
        Assert.NotNull(schema);
        Assert.Equal(0, completed);
        reader.Close();
        Assert.True(reader.IsClosed);
        Assert.Equal(0, completed);
    }

    [Fact]
    public void Ado_enumeration_does_not_claim_next_result_completion()
    {
        using var raw = new ContractReader(1);
        var completed = 0;
        using var reader = OwnerSqlExecution.ObserveReaderCompletion(raw, () => completed++, _ => { });
        var rows = 0;
        foreach (DbDataRecord row in reader)
        {
            Assert.Equal(17, row.GetInt32(0));
            rows++;
        }
        Assert.Equal(1, rows);
        Assert.Equal(0, completed);
        Assert.False(reader.NextResult());
        Assert.Equal(1, completed);
    }

    private static PostgresException ProviderFailure() => new("Synthetic provider refusal.", "ERROR", "ERROR", "42883");

    private static async Task<object?> Execute(string operation, DbCommand command, CancellationToken token) => operation switch
    {
        "reader" => await OwnerSqlExecution.ExecuteReaderAsync(command, token),
        "scalar" => await OwnerSqlExecution.ExecuteScalarAsync(command, token),
        "nonquery" => await OwnerSqlExecution.ExecuteNonQueryAsync(command, token),
        "bootstrap" => await OwnerSqlExecution.ExecuteBootstrapNonQueryAsync(command, token, "auth"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private sealed class EvidenceSetting : IDisposable
    {
        private readonly string? previous = Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE");
        public EvidenceSetting(string? value) => Environment.SetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE", value);
        public void Dispose() => Environment.SetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE", previous);
    }

    private sealed class ContractCommand : DbCommand
    {
        public int Dispatches { get; private set; }
        public Exception? Failure { get; init; }
        public DbDataReader Reader { get; } = new ContractReader(1);
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        private T Dispatch<T>(T value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Dispatches++;
            if (Failure is not null) throw Failure;
            return value;
        }
        public override int ExecuteNonQuery() => Dispatch(23, CancellationToken.None);
        public override object ExecuteScalar() => Dispatch<object>(17, CancellationToken.None);
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Dispatch(Reader, CancellationToken.None);
        public override Task<int> ExecuteNonQueryAsync(CancellationToken token) => Task.FromResult(Dispatch(23, token));
        public override Task<object?> ExecuteScalarAsync(CancellationToken token) => Task.FromResult(Dispatch<object?>(17, token));
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token) =>
            Task.FromResult(Dispatch(Reader, token));
    }

    private sealed class ContractReader(int results) : DbDataReader
    {
        private int row;
        private bool closed;
        public int ResultsFinished { get; private set; }
        public string FailureAt { get; set; } = "";
        public PostgresException? Failure { get; init; }
        public override bool Read()
        {
            if (FailureAt == "read" && row > 0) throw Failure!;
            return row++ == 0;
        }
        public override Task<bool> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Read());
        }
        public override bool NextResult()
        {
            if (FailureAt == "next") throw Failure!;
            row = 0;
            return ++ResultsFinished < results;
        }
        public override Task<bool> NextResultAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(NextResult());
        }
        public override object GetValue(int ordinal)
        {
            if (FailureAt == "value") throw Failure!;
            return 17;
        }
        public override void Close()
        {
            if (FailureAt == "close") throw Failure!;
            closed = true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && FailureAt == "dispose") throw Failure!;
            closed = true;
        }
        public override ValueTask DisposeAsync()
        {
            if (FailureAt == "dispose-async") throw Failure!;
            closed = true;
            return ValueTask.CompletedTask;
        }
        public override IEnumerator GetEnumerator() => new DbEnumerator(this, false);
        public override int Depth => 0;
        public override int FieldCount => 1;
        public override bool HasRows => true;
        public override bool IsClosed => closed;
        public override int RecordsAffected => -1;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(0);
        public override bool GetBoolean(int ordinal) => true;
        public override byte GetByte(int ordinal) => 17;
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int start, int count) => 0;
        public override char GetChar(int ordinal) => 'x';
        public override long GetChars(int ordinal, long offset, char[]? buffer, int start, int count) => 0;
        public override string GetDataTypeName(int ordinal) => "integer";
        public override DateTime GetDateTime(int ordinal) => DateTime.UnixEpoch;
        public override decimal GetDecimal(int ordinal) => 17;
        public override double GetDouble(int ordinal) => 17;
        public override Type GetFieldType(int ordinal) => typeof(int);
        public override float GetFloat(int ordinal) => 17;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 17;
        public override int GetInt32(int ordinal) => 17;
        public override long GetInt64(int ordinal) => 17;
        public override string GetName(int ordinal) => "value";
        public override int GetOrdinal(string name) => 0;
        public override string GetString(int ordinal) => "17";
        public override int GetValues(object[] values) { values[0] = GetValue(0); return 1; }
        public override bool IsDBNull(int ordinal) => false;
    }
}

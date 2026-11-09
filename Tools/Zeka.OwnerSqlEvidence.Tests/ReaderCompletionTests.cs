using System.Collections;
using System.Data;
using System.Data.Common;
using Npgsql;
using Xunit;
using Zeka.OwnerSqlEvidence;

public sealed class ReaderCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_results_must_complete_before_success(bool asynchronous)
    {
        var success=0; var failures=0;
        using var observed=OwnerSqlExecution.ObserveReaderCompletion(new ScriptedReader(2),()=>success++, _=>failures++);
        Assert.Equal(0,success);
        do
        {
            Assert.True(asynchronous?await observed.ReadAsync():observed.Read());
            Assert.False(asynchronous?await observed.ReadAsync():observed.Read());
            Assert.Equal(0,success);
        } while(asynchronous?await observed.NextResultAsync():observed.NextResult());
        Assert.Equal(1,success); Assert.Equal(0,failures);
        Assert.False(asynchronous?await observed.NextResultAsync():observed.NextResult());
        Assert.Equal(1,success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_disposal_is_not_success(bool readOneRow)
    {
        var success=0;
        var observed=OwnerSqlExecution.ObserveReaderCompletion(new ScriptedReader(1),()=>success++, _=>{});
        if(readOneRow) Assert.True(await observed.ReadAsync());
        await observed.DisposeAsync(); Assert.Equal(0,success);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task Deferred_provider_failure_is_preserved(bool nextResult,bool asynchronous)
    {
        var original=new PostgresException("synthetic provider refusal","ERROR","ERROR","42883");
        var raw=new ScriptedReader(1) { Failure=original, FailOnNext=nextResult };
        var success=0; PostgresException? captured=null;
        using var observed=OwnerSqlExecution.ObserveReaderCompletion(raw,()=>success++, error=>captured=error);
        if(nextResult)
        {
            if(asynchronous) Assert.Same(original,await Assert.ThrowsAsync<PostgresException>(()=>observed.NextResultAsync()));
            else Assert.Same(original,Assert.Throws<PostgresException>(()=>observed.NextResult()));
        }
        else
        {
            // The first row succeeds; the later row produces the actual deferred-error boundary.
            Assert.True(asynchronous?await observed.ReadAsync():observed.Read());
            if(asynchronous) Assert.Same(original,await Assert.ThrowsAsync<PostgresException>(()=>observed.ReadAsync()));
            else Assert.Same(original,Assert.Throws<PostgresException>(()=>observed.Read()));
        }
        Assert.Same(original,captured); Assert.Equal(0,success);
    }

    [Fact]
    public async Task Diagnostic_failure_cannot_replace_original_provider_failure()
    {
        var original=new PostgresException("synthetic provider refusal","ERROR","ERROR","42883");
        using var observed=OwnerSqlExecution.ObserveReaderCompletion(new ScriptedReader(1) {Failure=original},()=>{},_=>throw new IOException("synthetic collector refusal"));
        Assert.True(await observed.ReadAsync());
        Assert.Same(original,await Assert.ThrowsAsync<PostgresException>(()=>observed.ReadAsync()));
        Assert.True((bool)original.Data["OwnerSqlEvidenceCaptureFailed"]!);
    }

    [Fact]
    public async Task Completion_helper_consumes_all_remaining_results()
    {
        var successes=0;
        using var reader=OwnerSqlExecution.ObserveReaderCompletion(new ScriptedReader(2),()=>successes++, _=>{});
        Assert.True(await reader.ReadAsync());
        await OwnerSqlExecution.CompleteReaderAsync(reader,CancellationToken.None);
        Assert.Equal(1,successes);
    }

    [Fact]
    public async Task Completion_helper_keeps_deferred_error_and_cancellation()
    {
        var original=new PostgresException("synthetic provider refusal","ERROR","ERROR","42883");
        var successes=0;
        using var reader=OwnerSqlExecution.ObserveReaderCompletion(new ScriptedReader(1){Failure=original},()=>successes++, _=>{});
        Assert.Same(original,await Assert.ThrowsAsync<PostgresException>(()=>OwnerSqlExecution.CompleteReaderAsync(reader,CancellationToken.None)));
        Assert.Equal(0,successes);
        using var canceled=new CancellationTokenSource(); canceled.Cancel();
        using var other=OwnerSqlExecution.ObserveReaderCompletion(new ScriptedReader(1),()=>successes++,_=>{});
        await Assert.ThrowsAsync<OperationCanceledException>(()=>OwnerSqlExecution.CompleteReaderAsync(other,canceled.Token));
        Assert.Equal(0,successes);
    }
    // Pure reader lifecycle fixture; none of these tests claims PostgreSQL integration evidence.
    private sealed class ScriptedReader(int resultCount):DbDataReader
    {
        private int result; private int row; private bool closed;
        public PostgresException? Failure {get;init;}
        public bool FailOnNext {get;init;}
        public override bool Read() { if(Failure is not null && !FailOnNext && row>0) throw Failure; return row++==0; }
        public override Task<bool> ReadAsync(CancellationToken ct) {ct.ThrowIfCancellationRequested();return Task.FromResult(Read());}
        public override bool NextResult() {if(Failure is not null && FailOnNext)throw Failure;row=0;return ++result<resultCount;}
        public override Task<bool> NextResultAsync(CancellationToken ct) {ct.ThrowIfCancellationRequested();return Task.FromResult(NextResult());}
        public override void Close()=>closed=true;
        public override IEnumerator GetEnumerator()=>new DbEnumerator(this,false);
        public override int Depth=>0; public override int FieldCount=>1; public override bool HasRows=>true; public override bool IsClosed=>closed; public override int RecordsAffected=>-1;
        public override object this[int i]=>1; public override object this[string n]=>1;
        public override bool GetBoolean(int i)=>true;public override byte GetByte(int i)=>1;public override long GetBytes(int i,long o,byte[]? b,int s,int c)=>0;
        public override char GetChar(int i)=>'x';public override long GetChars(int i,long o,char[]? b,int s,int c)=>0;
        public override string GetDataTypeName(int i)=>"integer";public override DateTime GetDateTime(int i)=>DateTime.UnixEpoch;public override decimal GetDecimal(int i)=>1;
        public override double GetDouble(int i)=>1;public override Type GetFieldType(int i)=>typeof(int);public override float GetFloat(int i)=>1;public override Guid GetGuid(int i)=>Guid.Empty;
        public override short GetInt16(int i)=>1;public override int GetInt32(int i)=>1;public override long GetInt64(int i)=>1;public override string GetName(int i)=>"value";
        public override int GetOrdinal(string n)=>0;public override string GetString(int i)=>"1";public override object GetValue(int i)=>1;public override int GetValues(object[] values){values[0]=1;return 1;}
        public override bool IsDBNull(int i)=>false;
    }
}

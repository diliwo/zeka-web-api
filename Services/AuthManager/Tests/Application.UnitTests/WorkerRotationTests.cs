using AuthManager.Application.Workers;

namespace Application.UnitTests;

// Controlled application ports prove admission; actual Identity persistence is tested separately.
public sealed class WorkerRotationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_or_nonlocal_path_never_enters_store(bool enabled, bool local)
    {
        var port = new Port(); var options = Options(enabled, local);
        await Assert.ThrowsAsync<WorkerRotationException>(() => new WorkerRotationService(port, options).ExecuteAsync("prepare", Caller(options), Request(options)));
        Assert.Equal(0, port.Calls);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("session")]
    [InlineData("organisation")]
    [InlineData("operation")]
    [InlineData("phase")]
    public async Task Wrong_owner_scope_empty_identifiers_and_unknown_phases_fail_before_store(string change)
    {
        var port = new Port(); var options = Options(); var caller = Caller(options); var request = Request(options);
        if (change == "subject") caller = caller with { SubjectId = Guid.NewGuid() };
        if (change == "session") caller = caller with { SessionId = Guid.Empty };
        if (change == "organisation") request = request with { OrganisationId = Guid.NewGuid() };
        if (change == "operation") request = request with { OperationId = Guid.Empty };
        await Assert.ThrowsAsync<WorkerRotationException>(() => new WorkerRotationService(port, options)
            .ExecuteAsync(change == "phase" ? "automatic-renewal" : "prepare", caller, request));
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Finalization_requires_two_distinct_profiles_and_source_bound_successful_use_evidence()
    {
        var port = new Port(); var options = Options(); var service = new WorkerRotationService(port, options);
        var witnesses = Witnesses();
        Assert.False(WorkerRotationService.ValidWitnesses(new WorkerUseWitness[] { null!, witnesses[1] }));
        await Assert.ThrowsAsync<WorkerRotationException>(() => service.ExecuteAsync("finalize", Caller(options), Request(options), []));
        await Assert.ThrowsAsync<WorkerRotationException>(() => service.ExecuteAsync("finalize", Caller(options), Request(options), [witnesses[0], witnesses[0]]));
        await Assert.ThrowsAsync<WorkerRotationException>(() => service.ExecuteAsync("finalize", Caller(options), Request(options),
            [witnesses[0], witnesses[1] with { SuccessfulUseEvidenceRef = "" }]));
        await Assert.ThrowsAsync<WorkerRotationException>(() => service.ExecuteAsync("finalize", Caller(options), Request(options),
            [witnesses[0], witnesses[1] with { ProcessInstanceId = Guid.Empty }]));
        Assert.Equal(0, port.Calls);
        await service.ExecuteAsync("finalize", Caller(options), Request(options), witnesses);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public void Private_result_string_representation_never_contains_private_value()
    {
        var generated = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var item = new WorkerPreparedIdentity { Profile = WorkerProfiles.AdminAreaRetry, Token = generated, Generation = 1 };
        var result = new WorkerRotationResult { State = "PREPARED", Workers = [item] };
        Assert.False(item.ToString().Contains(generated, StringComparison.Ordinal));
        Assert.False(result.ToString().Contains(generated, StringComparison.Ordinal));
    }

    private static WorkerRotationOptions Options(bool enabled = true, bool local = true) => new()
    { Enabled = enabled, LocalStack = local, OwnerSubjectId = Guid.NewGuid(), OrganisationId = Guid.NewGuid(),
      ProcessingTimeout = TimeSpan.FromSeconds(120), Retention = TimeSpan.FromDays(1) };
    private static WorkerRotationCaller Caller(WorkerRotationOptions options) => new(options.OwnerSubjectId, Guid.NewGuid());
    private static WorkerRotationRequest Request(WorkerRotationOptions options) => new(Guid.NewGuid(), options.OrganisationId);
    private static WorkerUseWitness[] Witnesses() => WorkerProfiles.All.Select(profile => new WorkerUseWitness(profile, 1, Guid.NewGuid(),
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), "retained/use-1", 1,
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), Guid.NewGuid())).ToArray();
    private sealed class Port : IWorkerRotationStore
    {
        public int Calls;
        public Task<WorkerRotationResult> ExecuteAsync(string phase, WorkerRotationCaller caller, WorkerRotationRequest request,
            IReadOnlyList<WorkerUseWitness>? witnesses, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new WorkerRotationResult { State = "CONTROLLED_PORT" }); }
    }
}

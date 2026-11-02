using AuthManager.Application.Workers;

namespace Application.UnitTests;

// Controlled ports prove request admission only; actual Identity persistence is tested separately.
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
    [InlineData("begin-cutover")]
    [InlineData("finalize")]
    [InlineData("abort")]
    [InlineData("inspect")]
    public async Task Wrong_scope_and_superseded_persistent_phases_fail_before_store(string change)
    {
        var port = new Port(); var options = Options(); var caller = Caller(options); var request = Request(options);
        if (change == "subject") caller = caller with { SubjectId = Guid.NewGuid() };
        if (change == "session") caller = caller with { SessionId = Guid.Empty };
        if (change == "organisation") request = request with { OrganisationId = Guid.NewGuid() };
        if (change == "operation") request = request with { OperationId = Guid.Empty };
        var phase = change is "subject" or "session" or "organisation" or "operation" ? "prepare" : change;
        await Assert.ThrowsAsync<WorkerRotationException>(() => new WorkerRotationService(port, options).ExecuteAsync(phase, caller, request));
        Assert.Equal(0, port.Calls);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("null")]
    [InlineData("volume")]
    [InlineData("destination")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("directory-mode")]
    [InlineData("file-mode")]
    [InlineData("consumer")]
    [InlineData("mount")]
    [InlineData("payload")]
    [InlineData("publication")]
    public async Task Revoke_requires_both_exact_profile_and_protected_delivery_metadata(string defect)
    {
        var port = new Port(); var options = Options(); var deliveries = Deliveries(options.RegistryId);
        if (defect == "partial") deliveries = [deliveries[0]];
        if (defect == "duplicate") deliveries[1] = deliveries[0];
        if (defect == "null") deliveries[1] = null!;
        if (defect == "volume") deliveries[1] = deliveries[1] with { VolumeName = deliveries[0].VolumeName };
        if (defect == "destination") deliveries[1] = deliveries[1] with { DestinationPath = "/wrong/current.json" };
        if (defect == "uid") deliveries[1] = deliveries[1] with { OwnerUid = 0 };
        if (defect == "gid") deliveries[1] = deliveries[1] with { OwnerGid = 0 };
        if (defect == "directory-mode") deliveries[1] = deliveries[1] with { DirectoryMode = "0700" };
        if (defect == "file-mode") deliveries[1] = deliveries[1] with { Mode = "0644" };
        if (defect == "consumer") deliveries[1] = deliveries[1] with { ConsumerService = "adminarea" };
        if (defect == "mount") deliveries[1] = deliveries[1] with { ReadOnlyMount = false };
        if (defect == "payload") deliveries[1] = deliveries[1] with { PayloadSha256 = "" };
        if (defect == "publication") deliveries[1] = deliveries[1] with { PublicationSha256 = "" };
        await Assert.ThrowsAsync<WorkerRotationException>(() => new WorkerRotationService(port, options)
            .ExecuteAsync("revoke-others", Caller(options), Request(options), deliveries));
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Complete_delivery_metadata_reaches_store_without_any_adoption_or_process_witness()
    {
        var port = new Port(); var options = Options();
        await new WorkerRotationService(port, options).ExecuteAsync("revoke-others", Caller(options), Request(options), Deliveries(options.RegistryId));
        Assert.Equal(1, port.Calls);
        var requiresUsage = typeof(WorkerDeliveryReceipt).GetProperties().Any(property => property.Name.Contains("SuccessfulUse", StringComparison.Ordinal)
            || property.Name.Contains("Process", StringComparison.Ordinal));
        Assert.False(requiresUsage);
    }

    [Fact]
    public void Private_result_string_representation_never_contains_private_value()
    {
        var generated = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var item = new WorkerPreparedIdentity { Profile = WorkerProfiles.AdminAreaRetry, Token = generated, Generation = 1 };
        var result = new WorkerRotationResult { State = "ISSUED", Workers = [item] };
        Assert.False(item.ToString().Contains(generated, StringComparison.Ordinal));
        Assert.False(result.ToString().Contains(generated, StringComparison.Ordinal));
    }

    private static WorkerRotationOptions Options(bool enabled = true, bool local = true) => new()
    { Enabled = enabled, LocalStack = local, OwnerSubjectId = Guid.NewGuid(), OrganisationId = Guid.NewGuid(), RegistryId = Guid.NewGuid(),
      ProcessingTimeout = TimeSpan.FromSeconds(120), Retention = TimeSpan.FromDays(1) };
    private static WorkerRotationCaller Caller(WorkerRotationOptions options) => new(options.OwnerSubjectId, Guid.NewGuid());
    private static WorkerRotationRequest Request(WorkerRotationOptions options) => new(Guid.NewGuid(), options.OrganisationId);
    private static WorkerDeliveryReceipt[] Deliveries(Guid registry) => WorkerProfiles.All.Select(profile => new WorkerDeliveryReceipt(
        profile, Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Digest(), Digest(), WorkerRotationService.ExpectedVolume(profile, registry),
        "/run/zeka-worker-credential/current.json", 1654, 1654, "0500", "0400", profile == WorkerProfiles.AdminAreaRetry ? "adminarea" : "client", true)).ToArray();
    private static string Digest() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private sealed class Port : IWorkerRotationStore
    {
        public int Calls;
        public Task<WorkerRotationResult> ExecuteAsync(string phase, WorkerRotationCaller caller, WorkerRotationRequest request,
            IReadOnlyList<WorkerDeliveryReceipt>? deliveries, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new WorkerRotationResult { State = "CONTROLLED_PORT" }); }
    }
}

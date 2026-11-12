using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zeka.Extensions.MultiTenancy.Abstractions;
using ClientManagement.Infrastructure.Messaging;
using System.Net;
using System.Net.Http.Json;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using Xunit;
using Zeka.WorkerCredentials;
using Zeka.WorkerCredentials.Testing;

namespace Infrastructure.IntegrationTests;

public sealed class WorkerAuthorizationRetryTests
{
    private const string Profile = WorkerCredentialEncoding.ClientProfile;

    [Theory]
    [InlineData("401,200", TenantAccessOutcome.Authorized, 2)]
    [InlineData("401,401", TenantAccessOutcome.Unavailable, 2)]
    [InlineData("403", TenantAccessOutcome.Denied, 1)]
    [InlineData("401,403", TenantAccessOutcome.Denied, 2)]
    [InlineData("503,401,200", TenantAccessOutcome.Authorized, 2)]
    [InlineData("503,401,401", TenantAccessOutcome.Unavailable, 2)]
    public async Task Same_safe_Auth_query_rereads_once_only_for_401_with_unchanged_subject_scope_and_deadline(
        string sequence, TenantAccessOutcome expected, int expectedReads)
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        var statuses = sequence.Split(',').Select(int.Parse).ToArray();
        var handler = new SequenceHandler(state, organisation, statuses);
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        var client = new CurrentTenantAccessClient(http, state.Identity);
        var result = await client.ResolveAsync(WorkerCredentialFixture.Subject.ToString("D"), organisation, default);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(statuses.Length, handler.Calls);
        Assert.Equal(expectedReads, state.Reader.Reads);
        Assert.Single(handler.Paths.Distinct());
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
        if (expectedReads == 2) Assert.Equal(2, state.Operation.Snapshot.Generation);
        else Assert.Equal(1, state.Operation.Snapshot.Generation);
    }

    [Fact]
    public async Task Nonworker_401_retains_unauthenticated_single_send_and_no_file_reread()
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        var handler = new SequenceHandler(state, organisation, [401]);
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        var result = await new CurrentTenantAccessClient(http, new FixedIdentity(state.Operation.Snapshot.Token))
            .ResolveAsync(WorkerCredentialFixture.Subject.ToString("D"), organisation, default);
        Assert.Equal(TenantAccessOutcome.Unauthenticated, result.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, state.Reader.Reads);
    }

    [Fact]
    public async Task Parallel_operations_do_not_share_a_reload_budget_or_mutable_snapshot()
    {
        var organisation = Guid.NewGuid();
        var first = new State(organisation);
        var second = new State(organisation);
        first.Reader.Value = WorkerCredentialEncoding.Serialize(first.Next);
        second.Reader.Value = WorkerCredentialEncoding.Serialize(second.Next);
        Assert.True(await first.Identity.ReloadAfterUnauthorizedAsync(default));
        Assert.Equal(2, first.Operation.Snapshot.Generation);
        Assert.Equal(1, second.Operation.Snapshot.Generation);
        Assert.True(await second.Identity.ReloadAfterUnauthorizedAsync(default));
        Assert.False(await first.Identity.ReloadAfterUnauthorizedAsync(default));
        Assert.Equal(2, first.Reader.Reads);
        Assert.Equal(2, second.Reader.Reads);
    }

    [Fact]
    public async Task Cancellation_of_original_operation_cancels_reload_without_another_request()
    {
        var organisation = Guid.NewGuid();
        var old = WorkerCredentialFixture.Package([organisation], profile: Profile);
        var bytes = WorkerCredentialEncoding.Serialize(old);
        var reader = new BlockingReload(bytes);
        var provider = WorkerCredentialFixture.Provider(reader, [organisation], Profile);
        var operation = new WorkerCredentialOperation(provider, await provider.ReadAsync(default), organisation);
        var identity = new OperationIdentity(operation);
        var calls = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        using var cancellation = new CancellationTokenSource();
        var pending = new CurrentTenantAccessClient(http, identity)
            .ResolveAsync(WorkerCredentialFixture.Subject.ToString("D"), organisation, cancellation.Token);
        await reader.ReloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, calls);
        Assert.Equal(2, reader.Reads);
    }

    [Theory]
    [InlineData("401,204", true, null, 2)]
    [InlineData("401,401", false, AccessFailure.Unavailable, 2)]
    [InlineData("403", false, null, 1)]
    public async Task Membership_GET_uses_the_same_one_reload_budget_and_preserves_denial(
        string sequence, bool expected, AccessFailure? failure, int expectedReads)
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        var statuses = sequence.Split(',').Select(int.Parse).ToArray();
        var handler = new SequenceHandler(state, organisation, statuses);
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        var call = new StaffMembershipClient(http, state.Identity).VerifyAsync(organisation, Guid.NewGuid(), true, default);
        if (failure.HasValue)
        {
            var error = await Assert.ThrowsAsync<TenantAccessException>(() => call);
            Assert.Equal(failure.Value, error.Failure);
        }
        else Assert.Equal(expected, await call);
        Assert.Equal(statuses.Length, handler.Calls);
        Assert.Equal(expectedReads, state.Reader.Reads);
        Assert.Single(handler.Paths.Distinct());
    }

    [Fact]
    public async Task Tenant_check_and_membership_check_share_one_total_401_reload()
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        var handler = new SequenceHandler(state, organisation, [401, 200, 401]);
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        var accepted = await new CurrentTenantAccessClient(http, state.Identity)
            .ResolveAsync(WorkerCredentialFixture.Subject.ToString("D"), organisation, default);
        Assert.Equal(TenantAccessOutcome.Authorized, accepted.Outcome);
        var denied = await Assert.ThrowsAsync<TenantAccessException>(() => new StaffMembershipClient(http, state.Identity)
            .VerifyAsync(organisation, Guid.NewGuid(), true, default));
        Assert.Equal(AccessFailure.Unavailable, denied.Failure);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, state.Reader.Reads);
    }

    [Fact]
    public async Task Already_inflight_old_request_rereads_published_file_after_revocation_401_inside_same_operation()
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        var oldHash = WorkerCredentialFixture.TokenSha256(state.Operation.Snapshot.Token);
        var newHash = WorkerCredentialFixture.TokenSha256(state.Next.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var paths = new List<string>();
        using var handler = new CallbackHandler(async (request, cancellationToken) =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            if (Interlocked.Increment(ref calls) == 1)
            {
                Assert.True(oldHash == WorkerCredentialFixture.TokenSha256(request.Headers.Authorization?.Parameter));
                started.SetResult(); // The original request is already in flight using the old credential.
                await revoked.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            Assert.True(newHash == WorkerCredentialFixture.TokenSha256(request.Headers.Authorization?.Parameter));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    ContractVersion = 1, SubjectId = WorkerCredentialFixture.Subject.ToString("D"),
                    OrganisationId = organisation, OrganisationMembershipId = Guid.NewGuid(),
                    EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" },
                    DecisionVersion = "replacement", ObservedAtUtc = DateTimeOffset.UtcNow
                })
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        var pending = new CurrentTenantAccessClient(http, state.Identity)
            .ResolveAsync(WorkerCredentialFixture.Subject.ToString("D"), organisation, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.Reader.Value = WorkerCredentialEncoding.Serialize(state.Next);
        revoked.SetResult(); // Simulated Auth revocation response; no real Auth/DB is claimed.
        var result = await pending;
        Assert.Equal(TenantAccessOutcome.Authorized, result.Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(2, state.Reader.Reads);
        Assert.Equal(2, state.Operation.Snapshot.Generation);
        Assert.Single(paths.Distinct());
    }
    [Fact]
    public async Task Actual_consumer_second401_is_transient_and_preserves_original_EventId_without_business_retry()
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        var handler = new SequenceHandler(state, organisation, [401, 401]);
        var services = new ServiceCollection().AddLogging().AddScoped<TenantContextScope>()
            .AddSingleton<IHttpClientFactory>(new ClientFactory(handler));
        await using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationManager
        { ["TenantAuthorization:AuthManagementUrl"] = WorkerCredentialFixture.Issuer };
        var consumer = new StaffProjectionConsumer(provider.GetRequiredService<IServiceScopeFactory>(),
            configuration, state.Credentials);
        var message = new Zeka.Contracts.Staff.V1.StaffProjectionChangedV1(organisation, Guid.NewGuid(), 1, true,
            "Generated", "Fixture", "fixture", "Team", "T");
        var originalEvent = message.Id;
        var readsBeforeDelivery = state.Reader.Reads;
        var failure = await Assert.ThrowsAsync<TenantAccessException>(() => consumer.Handle(message));
        Assert.Equal(AccessFailure.Unavailable, failure.Failure);
        Assert.Equal(originalEvent, message.Id);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(readsBeforeDelivery + 2, state.Reader.Reads);
        Assert.Single(handler.Paths.Distinct());
        // No DB is registered: a failed authorization cannot reach persistence or a business-message retry.
        // Actual broker ACK/NACK/redelivery and delivery-limit5 proof remain blocked on accepted ZEK-22.
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    [Fact]
    public async Task Unreadable_replacement_never_retries_with_cached_old_credential()
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        state.Reader.Value = [];
        var calls = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        var result = await new CurrentTenantAccessClient(http, state.Identity)
            .ResolveAsync(WorkerCredentialFixture.Subject.ToString("D"), organisation, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(1, calls);
        Assert.Equal(2, state.Reader.Reads);
        Assert.Equal(1, state.Operation.Snapshot.Generation);
        Assert.False(await state.Identity.ReloadAfterUnauthorizedAsync(default));
    }

    [Fact]
    public async Task Auth_client_itself_caps_401_retry_if_opt_in_context_violates_its_budget()
    {
        var organisation = Guid.NewGuid();
        var state = new State(organisation);
        var identity = new UnboundedOptIn(state.Operation.Snapshot.Token);
        var calls = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        using var http = new HttpClient(handler) { BaseAddress = new(WorkerCredentialFixture.Issuer) };
        var result = await new CurrentTenantAccessClient(http, identity)
            .ResolveAsync(WorkerCredentialFixture.Subject.ToString("D"), organisation, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(1, identity.Reloads);
    }

    private sealed class UnboundedOptIn(string token) : ITenantAccessCredential, IWorkerCredential401Retry
    {
        internal int Reloads;
        public string? BearerToken => token;
        public ValueTask<bool> ReloadAfterUnauthorizedAsync(CancellationToken cancellationToken)
        { Reloads++; return ValueTask.FromResult(true); }
    }
    private sealed class State
    {
        internal readonly WorkerCredentialFixture.Bytes Reader;
        internal readonly WorkerCredentialPackage Next;
        internal readonly IWorkerCredentialProvider Credentials;
        internal readonly WorkerCredentialOperation Operation;
        internal readonly OperationIdentity Identity;
        internal State(Guid organisation)
        {
            var original = WorkerCredentialFixture.Package([organisation], profile: Profile);
            Next = WorkerCredentialFixture.Package([organisation], 2, Profile);
            Reader = new(WorkerCredentialEncoding.Serialize(original));
            var provider = WorkerCredentialFixture.Provider(Reader, [organisation], Profile);
            Credentials = provider;
            Operation = new(provider, provider.ReadAsync(default).AsTask().GetAwaiter().GetResult(), organisation);
            Identity = new(Operation);
        }
    }

    private sealed class OperationIdentity(WorkerCredentialOperation operation) : ITenantAccessCredential, IWorkerCredential401Retry
    {
        public string? BearerToken => operation.Snapshot.Token;
        public ValueTask<bool> ReloadAfterUnauthorizedAsync(CancellationToken cancellationToken) =>
            operation.ReloadAfterUnauthorizedAsync(cancellationToken);
    }
    private sealed record FixedIdentity(string? BearerToken) : ITenantAccessCredential;
    private sealed class SequenceHandler(State state, Guid organisation, int[] statuses) : HttpMessageHandler
    {
        internal int Calls;
        internal readonly List<string> Paths = [];
        internal readonly List<HttpMethod> Methods = [];
        private bool published;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Calls < statuses.Length);
            Paths.Add(request.RequestUri!.PathAndQuery);
            Methods.Add(request.Method);
            var expected = published ? state.Next.Token : state.Operation.Snapshot.Token;
            Assert.True(WorkerCredentialFixture.TokenSha256(expected)
                == WorkerCredentialFixture.TokenSha256(request.Headers.Authorization?.Parameter));
            var status = statuses[Calls++];
            if (status == 401)
            {
                state.Reader.Value = WorkerCredentialEncoding.Serialize(state.Next);
                published = true;
            }
            if (status == 200)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        ContractVersion = 1, SubjectId = WorkerCredentialFixture.Subject.ToString("D"),
                        OrganisationId = organisation, OrganisationMembershipId = Guid.NewGuid(),
                        EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" },
                        DecisionVersion = "generated", ObservedAtUtc = DateTimeOffset.UtcNow
                    })
                });
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        }
    }
    private sealed class BlockingReload(byte[] first) : IProtectedWorkerCredentialReader
    {
        internal int Reads;
        internal readonly TaskCompletionSource ReloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Reads) == 1) return first.ToArray();
            ReloadStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("UNREACHABLE");
        }
    }
    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}

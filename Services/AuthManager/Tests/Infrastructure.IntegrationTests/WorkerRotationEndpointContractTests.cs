using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authentication;
using AuthManager.Application.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

// Direct handler/route contracts only: no authentication handler, token, TLS, database,
// credential issuance, server startup or worker-runtime acceptance is simulated here.
public sealed class WorkerRotationEndpointContractTests
{
    private static readonly MethodInfo Handler = typeof(WorkerRotationEndpoints).GetMethod(
        "ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("missing-subject")]
    [InlineData("ambiguous-subject")]
    [InlineData("invalid-subject")]
    [InlineData("empty-subject")]
    [InlineData("invalid-session")]
    [InlineData("empty-session")]
    public void Caller_identifiers_must_be_unambiguous_nonempty_GUIDs(string defect)
    {
        var claims = new List<Claim>
        {
            new("sub", Guid.NewGuid().ToString("D")), new("sid", Guid.NewGuid().ToString("D"))
        };
        if (defect == "missing-subject") claims.RemoveAt(0);
        if (defect == "ambiguous-subject") claims.Add(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")));
        if (defect == "invalid-subject") claims[0] = new Claim("sub", "not-a-guid");
        if (defect == "empty-subject") claims[0] = new Claim("sub", Guid.Empty.ToString("D"));
        if (defect == "invalid-session") claims[1] = new Claim("sid", "not-a-guid");
        if (defect == "empty-session") claims[1] = new Claim("sid", Guid.Empty.ToString("D"));
        Assert.False(WorkerRotationEndpoints.TryCaller(new ClaimsPrincipal(new ClaimsIdentity(claims, "Direct-caller-contract")), out var caller));
        Assert.Equal(new WorkerRotationCaller(Guid.Empty, Guid.Empty), caller);
    }

    [Fact]
    public void Equivalent_authenticated_subject_aliases_are_admitted_but_unauthed_identity_claims_are_ignored()
    {
        var subject = Guid.NewGuid(); var session = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new[]
        {
            new ClaimsIdentity(new[] { new Claim("sub", Guid.NewGuid().ToString("D")), new Claim("sid", Guid.NewGuid().ToString("D")) }),
            new ClaimsIdentity(new[] { new Claim("sub", subject.ToString("D")), new Claim(ClaimTypes.NameIdentifier, subject.ToString("D")),
                new Claim("sid", session.ToString("D")) }, "Direct-caller-contract")
        });
        Assert.True(WorkerRotationEndpoints.TryCaller(principal, out var caller));
        Assert.Equal(new WorkerRotationCaller(subject, session), caller);
    }

    [Theory]
    [InlineData("Production", true, 0)]
    [InlineData("Development", true, 0)]
    [InlineData("LocalStack", false, 0)]
    [InlineData("LocalStack", true, 2)]
    public async Task Routes_exist_only_for_explicit_LocalStack_enablement_and_require_bearer_authorization(
        string environment, bool enabled, int expected)
    {
        var fixture = new Fixture();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["M1WorkerRotation:Enabled"] = enabled.ToString()
        });
        builder.Services.AddSingleton(fixture.Service);
        builder.Services.AddSingleton<IBrowserSessions>(fixture.Sessions);
        await using var app = builder.Build();
        app.MapWorkerRotation();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.Equal(expected, endpoints.Length);
        if (expected == 0) return;
        Assert.Equal(new[] { "/internal/m1/worker-rotation/prepare", "/internal/m1/worker-rotation/revoke-others" },
            endpoints.Select(endpoint => endpoint.RoutePattern.RawText).OrderBy(path => path));
        Assert.All(endpoints, endpoint =>
        {
            var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(JwtBearerDefaults.AuthenticationScheme, authorization.AuthenticationSchemes);
            Assert.Equal(new[] { "POST" }, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        });
        Assert.Equal(0, fixture.Sessions.Calls);
        Assert.Equal(0, fixture.Store.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_HTTPS_or_authenticated_caller_is_refused_before_session_or_store_access(bool https, bool authenticated)
    {
        var fixture = new Fixture();
        fixture.Http.Request.Scheme = https ? "https" : "http";
        if (!authenticated) fixture.Http.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await fixture.InvokeAsync();
        Assert.Equal(401, Status(result));
        Assert.Equal(0, fixture.Sessions.Calls);
        Assert.Equal(0, fixture.Store.Calls);
        AssertNoStore(fixture.Http);
    }

    [Theory]
    [InlineData(BrowserSessionOutcome.Unauthenticated, 401)]
    [InlineData(BrowserSessionOutcome.Unavailable, 503)]
    public async Task Live_session_denial_preserves_unauthorized_versus_dependency_unavailable(
        BrowserSessionOutcome outcome, int expected)
    {
        var fixture = new Fixture();
        fixture.Sessions.Outcome = outcome;
        var result = await fixture.InvokeAsync();
        Assert.Equal(expected, Status(result));
        Assert.Equal(1, fixture.Sessions.Calls);
        Assert.Equal(fixture.Owner, fixture.Sessions.Subject);
        Assert.Equal(fixture.Session, fixture.Sessions.Session);
        Assert.True(fixture.Sessions.Token.CanBeCanceled);
        Assert.Equal(0, fixture.Store.Calls);
        AssertNoStore(fixture.Http);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("provider")]
    public async Task Session_failure_never_continues_to_worker_execution_or_exports_an_exception(string failure)
    {
        var fixture = new Fixture();
        fixture.Sessions.Failure = failure == "cancelled" ? new OperationCanceledException() : new IOException();
        var result = await fixture.InvokeAsync();
        Assert.Equal(503, Status(result));
        Assert.False(result is IValueHttpResult);
        Assert.Equal(0, fixture.Store.Calls);
        AssertNoStore(fixture.Http);
    }

    [Theory]
    [InlineData(false, 409, "CONFLICT")]
    [InlineData(true, 503, "RECOVERY_REQUIRED")]
    public async Task Worker_conflict_and_uncertain_state_keep_distinct_safe_transport_results(bool inspection, int expected, string state)
    {
        var fixture = new Fixture();
        fixture.Store.Failure = new WorkerRotationException("WORKER_ROTATION_OWNER_SCOPE_REJECTED", inspection);
        var result = await fixture.InvokeAsync();
        Assert.Equal(expected, Status(result));
        Assert.Equal(1, fixture.Store.Calls);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value));
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(state, body.RootElement.GetProperty("state").GetString());
        Assert.Equal("WORKER_ROTATION_OWNER_SCOPE_REJECTED", body.RootElement.GetProperty("code").GetString());
        AssertNoStore(fixture.Http);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("provider")]
    public async Task Worker_failure_returns_no_private_body_and_does_not_retry_the_store(string failure)
    {
        var fixture = new Fixture();
        fixture.Store.Failure = failure == "cancelled" ? new OperationCanceledException() : new IOException();
        var result = await fixture.InvokeAsync();
        Assert.Equal(503, Status(result));
        Assert.False(result is IValueHttpResult);
        Assert.Equal(1, fixture.Store.Calls);
        AssertNoStore(fixture.Http);
    }

    [Fact]
    public async Task Admitted_session_forwards_the_exact_request_once_with_linked_cancellation()
    {
        var fixture = new Fixture();
        var result = await fixture.InvokeAsync();
        Assert.Same(fixture.Store.Result, Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var responseBody = new MemoryStream();
        fixture.Http.RequestServices = services;
        fixture.Http.Response.Body = responseBody;
        await result.ExecuteAsync(fixture.Http);
        Assert.Equal(200, fixture.Http.Response.StatusCode);
        Assert.StartsWith("application/json", fixture.Http.Response.ContentType);
        responseBody.Position = 0;
        using var body = JsonDocument.Parse(responseBody);
        Assert.Equal(fixture.Store.Result.State, body.RootElement.GetProperty("state").GetString());
        Assert.Equal(1, fixture.Sessions.Calls);
        Assert.Equal(1, fixture.Store.Calls);
        Assert.Equal("prepare", fixture.Store.Phase);
        Assert.Equal(new WorkerRotationCaller(fixture.Owner, fixture.Session), fixture.Store.Caller);
        Assert.Same(fixture.Request, fixture.Store.Request);
        Assert.True(fixture.Store.Token.CanBeCanceled);
        AssertNoStore(fixture.Http);
    }

    [Fact]
    public async Task Aborted_request_is_unavailable_without_any_worker_continuation()
    {
        var fixture = new Fixture();
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        fixture.Http.RequestAborted = aborted.Token;
        var result = await fixture.InvokeAsync();
        Assert.Equal(503, Status(result));
        Assert.Equal(0, fixture.Store.Calls);
        AssertNoStore(fixture.Http);
    }

    private static int Status(IResult result) =>
        Assert.IsType<int>(Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

    private static void AssertNoStore(HttpContext http)
    {
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", http.Response.Headers.Pragma.ToString());
    }

    private sealed class Fixture
    {
        internal readonly Guid Owner = Guid.NewGuid();
        internal readonly Guid Session = Guid.NewGuid();
        internal readonly DefaultHttpContext Http = new();
        internal readonly SessionProbe Sessions = new();
        internal readonly StoreProbe Store = new();
        internal readonly WorkerRotationRequest Request;
        internal readonly WorkerRotationService Service;

        internal Fixture()
        {
            var organisation = Guid.NewGuid();
            Request = new(Guid.NewGuid(), organisation);
            Service = new(Store, new WorkerRotationOptions
            {
                Enabled = true, LocalStack = true, OwnerSubjectId = Owner, OrganisationId = organisation,
                RegistryId = Guid.NewGuid(), ProcessingTimeout = TimeSpan.FromMinutes(2), Retention = TimeSpan.FromDays(1)
            });
            Http.Request.Scheme = "https";
            Http.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("sub", Owner.ToString("D")), new Claim("sid", Session.ToString("D"))
            }, "Direct-handler-unit-contract"));
        }

        internal Task<IResult> InvokeAsync() => (Task<IResult>)Handler.Invoke(null,
            new object?[] { "prepare", Request, null, Http, Service, Sessions })!;
    }

    private sealed class StoreProbe : IWorkerRotationStore
    {
        internal int Calls;
        internal Exception? Failure;
        internal string? Phase;
        internal WorkerRotationCaller? Caller;
        internal WorkerRotationRequest? Request;
        internal CancellationToken Token;
        internal readonly WorkerRotationResult Result = new() { State = "UNIT_CONTRACT_NO_ISSUANCE" };

        public Task<WorkerRotationResult> ExecuteAsync(string phase, WorkerRotationCaller caller, WorkerRotationRequest request,
            IReadOnlyList<WorkerDeliveryReceipt>? deliveries, CancellationToken cancellationToken)
        {
            Calls++; Phase = phase; Caller = caller; Request = request; Token = cancellationToken;
            if (Failure is not null) throw Failure;
            return Task.FromResult(Result);
        }
    }

    private sealed class SessionProbe : IBrowserSessions
    {
        internal int Calls;
        internal Guid Subject;
        internal Guid Session;
        internal CancellationToken Token;
        internal Exception? Failure;
        internal BrowserSessionOutcome Outcome = BrowserSessionOutcome.Success;

        public Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken)
        {
            Calls++; Subject = subjectId; Session = sessionId; Token = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Task.FromResult(Outcome);
        }

        public Task<BrowserSessionResult> SignInAsync(string email, string password, string? previousCookie, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No sign-in is performed by these handler contracts.");
        public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No restore is performed by these handler contracts.");
        public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No logout is performed by these handler contracts.");
    }
}

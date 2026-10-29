using System.Net;
using System.Text.Encodings.Web;
using ClientManagement.API;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.IntegrationTests;

// Runs the application's real adapter over Kestrel. Explicit fixture checks exercise
// its selection/status behavior; this does not assert database or broker readiness.
public sealed class LocalStackHealthTests
{
    [Theory]
    [InlineData("LocalStack", true)]
    [InlineData("Development", false)]
    [InlineData("Production", false)]
    public void Health_registration_is_local_only_and_does_not_invent_dependency_checks(
        string environment, bool expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();

        LocalStackHealth.Add(builder);

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetService<HealthCheckService>() is not null);
        Assert.Empty(provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
    }

    [Fact]
    public async Task Liveness_returns_only_status_without_executing_any_check()
    {
        await using var host = await StartAsync("LocalStack", HealthStatus.Unhealthy);
        host.Ready.Throws = true;
        host.Untagged.Throws = true;

        await AssertStatus(host, "/health/live", HttpStatusCode.OK);

        Assert.Equal(0, host.Ready.Calls);
        Assert.Equal(0, host.Untagged.Calls);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HttpStatusCode.OK)]
    [InlineData(HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HealthStatus.Degraded, HttpStatusCode.ServiceUnavailable)]
    public async Task Readiness_uses_registered_ready_checks_and_sanitizes_the_response(
        HealthStatus status, HttpStatusCode expected)
    {
        await using var host = await StartAsync("LocalStack", status);

        await AssertStatus(host, "/health/ready", expected);

        Assert.Equal(1, host.Ready.Calls);
        Assert.Equal(0, host.Untagged.Calls);
    }

    [Fact]
    public async Task Readiness_rechecks_current_status_and_ignores_untagged_failures()
    {
        await using var host = await StartAsync("LocalStack", HealthStatus.Healthy);
        host.Untagged.Throws = true;

        await AssertStatus(host, "/health/ready", HttpStatusCode.OK);
        host.Ready.Status = HealthStatus.Unhealthy;
        await AssertStatus(host, "/health/ready", HttpStatusCode.ServiceUnavailable);
        host.Ready.Status = HealthStatus.Degraded;
        await AssertStatus(host, "/health/ready", HttpStatusCode.ServiceUnavailable);
        host.Ready.Status = HealthStatus.Healthy;
        await AssertStatus(host, "/health/ready", HttpStatusCode.OK);

        Assert.Equal(4, host.Ready.Calls);
        Assert.Equal(0, host.Untagged.Calls);
    }

    [Fact]
    public async Task Check_exceptions_fail_readiness_without_exposing_details()
    {
        await using var host = await StartAsync("LocalStack", HealthStatus.Healthy);
        host.Ready.Throws = true;

        await AssertStatus(host, "/health/ready", HttpStatusCode.ServiceUnavailable);
        await AssertStatus(host, "/health/live", HttpStatusCode.OK);

        Assert.Equal(1, host.Ready.Calls);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Other_environments_do_not_bypass_business_authentication(string environment)
    {
        await using var host = await StartAsync(environment, HealthStatus.Healthy);

        await AssertStatus(host, "/health/live", HttpStatusCode.Unauthorized);
        await AssertStatus(host, "/health/ready", HttpStatusCode.Unauthorized);

        Assert.Equal(0, host.Ready.Calls);
        Assert.Equal(0, host.Untagged.Calls);
    }

    [Fact]
    public async Task LocalStack_business_routes_still_require_authentication()
    {
        await using var host = await StartAsync("LocalStack", HealthStatus.Healthy);

        await AssertStatus(host, "/business", HttpStatusCode.Unauthorized);
        await AssertStatus(host, "/health/live", HttpStatusCode.OK);
        await AssertStatus(host, "/health/ready", HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health/live/child")]
    [InlineData("/health/ready/child")]
    public async Task Probe_prefixes_do_not_open_neighboring_routes(string path)
    {
        await using var host = await StartAsync("LocalStack", HealthStatus.Healthy);

        await AssertStatus(host, path, HttpStatusCode.Unauthorized);

        Assert.Equal(0, host.Ready.Calls);
    }

    private static async Task AssertStatus(ProbeHost host, string path, HttpStatusCode status)
    {
        using var response = await host.Http.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    private static async Task<ProbeHost> StartAsync(string environment, HealthStatus status)
    {
        var ready = new MutableCheck { Status = status };
        var untagged = new MutableCheck { Status = HealthStatus.Unhealthy };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        LocalStackHealth.Add(builder);
        builder.Services.AddHealthChecks()
            .Add(new HealthCheckRegistration("fixture-ready", ready, HealthStatus.Unhealthy, ["ready"]))
            .Add(new HealthCheckRegistration("fixture-untagged", untagged, HealthStatus.Unhealthy, []));
        builder.Services.AddAuthentication("no-credentials")
            .AddScheme<AuthenticationSchemeOptions, NoCredentialsHandler>("no-credentials", _ => { });
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseLocalStackHealth();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/business", () => Results.NoContent()).RequireAuthorization();
        app.MapFallback(() => Results.NotFound()).RequireAuthorization();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(timeout.Token);
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(5)
            };
            return new ProbeHost(app, http, ready, untagged);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private sealed class MutableCheck : IHealthCheck
    {
        private int _calls;
        public HealthStatus Status { get; set; }
        public bool Throws { get; set; }
        public int Calls => Volatile.Read(ref _calls);

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (Throws) throw new InvalidOperationException("private fixture exception detail");
            return Task.FromResult(new HealthCheckResult(Status, "private fixture dependency detail"));
        }
    }

    private sealed class NoCredentialsHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }

    private sealed record ProbeHost(
        WebApplication App, HttpClient Http, MutableCheck Ready, MutableCheck Untagged) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await App.StopAsync(timeout.Token); }
            finally { await App.DisposeAsync(); }
        }
    }
}

using System.Net;
using AuthManager.API;
using AuthManager.Infrastructure.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Infrastructure.IntegrationTests;

// Exercises the production health adapter over loopback Kestrel without the API's
// database bootstrap, credentials, signing files or external JWKS provider.
public sealed class LocalStackHealthTests
{
    [Fact]
    public async Task Liveness_does_not_claim_dependency_readiness()
    {
        await using var host = await StartAsync("LocalStack", issuerReady: false, HealthStatus.Unhealthy);
        await AssertStatus(host, "/health/live", HttpStatusCode.OK);
        await AssertStatus(host, "/health/ready", HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Readiness_rechecks_issuer_state_instead_of_caching_a_healthy_result()
    {
        await using var host = await StartAsync("LocalStack", issuerReady: true, HealthStatus.Healthy);
        await AssertStatus(host, "/health/ready", HttpStatusCode.OK);
        host.Issuer.Ready = false;
        await AssertStatus(host, "/health/ready", HttpStatusCode.ServiceUnavailable);
        host.Issuer.Ready = true;
        await AssertStatus(host, "/health/ready", HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded)]
    public async Task Dependency_failure_is_not_reported_healthy_or_disclosed(HealthStatus dependency)
    {
        await using var host = await StartAsync("LocalStack", issuerReady: true, dependency);
        await AssertStatus(host, "/health/ready", HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Issuer_exception_fails_closed_without_response_details()
    {
        await using var host = await StartAsync("LocalStack", issuerReady: true, HealthStatus.Healthy);
        host.Issuer.Throw = true;
        await AssertStatus(host, "/health/ready", HttpStatusCode.ServiceUnavailable);
        await AssertStatus(host, "/health/live", HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Other_environments_do_not_get_the_local_health_transport_bypass(string environment)
    {
        await using var host = await StartAsync(environment, issuerReady: true, HealthStatus.Healthy);
        using var live = await host.Http.GetAsync("/health/live");
        using var ready = await host.Http.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, live.StatusCode);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, ready.StatusCode);
        Assert.Equal("https", live.Headers.Location!.Scheme);
        Assert.Equal("https", ready.Headers.Location!.Scheme);
    }

    [Fact]
    public async Task Business_transport_still_redirects_in_LocalStack()
    {
        await using var host = await StartAsync("LocalStack", issuerReady: true, HealthStatus.Healthy);
        using var business = await host.Http.GetAsync("/business");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, business.StatusCode);
        Assert.Equal("https", business.Headers.Location!.Scheme);
        await AssertStatus(host, "/health/live", HttpStatusCode.OK);
    }

    private static async Task AssertStatus(ProbeHost host, string path, HttpStatusCode status)
    {
        using var response = await host.Http.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    private static async Task<ProbeHost> StartAsync(string environment, bool issuerReady, HealthStatus dependency)
    {
        var issuer = new MutableIssuer { Ready = issuerReady };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IAccessTokenIssuer>(issuer);
        LocalStackHealth.Add(builder);
        builder.Services.AddHealthChecks().AddCheck("synthetic-dependency",
            () => new HealthCheckResult(dependency, "synthetic internal dependency detail"), tags: ["ready"]);
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
        var app = builder.Build();
        app.UseLocalStackHealth();
        app.UseHttpsRedirection();
        app.Run(context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });
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
            return new ProbeHost(app, http, issuer);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private sealed class MutableIssuer : IAccessTokenIssuer
    {
        public bool Ready { get; set; }
        public bool Throw { get; set; }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        {
            if (Throw) throw new InvalidOperationException("synthetic internal issuer detail");
            return Task.FromResult(Ready);
        }
        public Task<string> IssueAsync(string subject, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed record ProbeHost(WebApplication App, HttpClient Http, MutableIssuer Issuer) : IAsyncDisposable
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

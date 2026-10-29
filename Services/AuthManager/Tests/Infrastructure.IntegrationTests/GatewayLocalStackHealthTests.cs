using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ocelot.Configuration.File;
using Xunit;
using Zeka.Gateway;

namespace Infrastructure.IntegrationTests;

// Runs the production registration, options validation and middleware in Kestrel.
// The fixture CA is trusted only by this client; no certificate trust store is changed.
public sealed class GatewayLocalStackHealthTests
{
    private const string Origin = "https://browser.example.invalid";
    private const string CsrfPath = "/api/v1/browser-session/csrf";

    [Fact]
    public async Task LocalStack_registers_the_gateway_check_and_status_only_health_routes()
    {
        await using var host = await StartAsync();
        var registration = Assert.Single(host.App.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations, item => item.Name == "gateway-browser-transport");
        Assert.Contains("ready", registration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        await AssertHealthAsync(host.Http, "/health/live", HttpStatusCode.OK);
        await AssertHealthAsync(host.Http, "/health/ready", HttpStatusCode.OK);
        Assert.Empty(host.Received);
        Assert.Equal(6, host.Routes.Routes.Count(route => route.Key!.StartsWith("GatewayBrowserTransport:")));
        Assert.All(host.Routes.Routes.Where(route => route.Key!.StartsWith("GatewayBrowserTransport:")), route =>
        {
            Assert.Equal("https", route.DownstreamScheme);
            var destination = Assert.Single(route.DownstreamHostAndPorts);
            Assert.Equal("auth.example.invalid", destination.Host);
            Assert.Equal(443, destination.Port);
        });
        var legacy = Assert.Single(host.Routes.Routes, route => route.Key == "legacy");
        Assert.Equal("legacy.example.invalid", Assert.Single(legacy.DownstreamHostAndPorts).Host);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HttpStatusCode.OK)]
    [InlineData(HealthStatus.Degraded, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HealthStatus.Unhealthy, HttpStatusCode.ServiceUnavailable)]
    public async Task Readiness_uses_ready_tagged_checks_and_sanitizes_their_details(
        HealthStatus dependency, HttpStatusCode expected)
    {
        await using var host = await StartAsync(dependency: () => dependency);
        await AssertHealthAsync(host.Http, "/health/live", HttpStatusCode.OK);
        await AssertHealthAsync(host.Http, "/health/ready", expected);
    }

    [Fact]
    public async Task Readiness_rechecks_current_registered_health_without_caching_success()
    {
        var state = HealthStatus.Healthy;
        await using var host = await StartAsync(dependency: () => state);
        await AssertHealthAsync(host.Http, "/health/ready", HttpStatusCode.OK);
        state = HealthStatus.Unhealthy;
        await AssertHealthAsync(host.Http, "/health/ready", HttpStatusCode.ServiceUnavailable);
        state = HealthStatus.Healthy;
        await AssertHealthAsync(host.Http, "/health/ready", HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Other_environments_do_not_register_or_expose_LocalStack_health(string environment)
    {
        await using var host = await StartAsync(environment);
        Assert.DoesNotContain(host.App.Services.GetServices<IConfigureOptions<HealthCheckServiceOptions>>()
            .SelectMany(options =>
            {
                var value = new HealthCheckServiceOptions();
                options.Configure(value);
                return value.Registrations;
            }), item => item.Name == "gateway-browser-transport");
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var response = await host.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var browser = await host.Http.GetAsync(CsrfPath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, browser.StatusCode);
    }

    [Theory]
    [InlineData("GatewayBrowserTransport:Origin", null)]
    [InlineData("GatewayBrowserTransport:Origin", "http://browser.example.invalid")]
    [InlineData("GatewayBrowserTransport:Origin", "https://browser.example.invalid/prefix")]
    [InlineData("GatewayBrowserTransport:Origin", "https://user@browser.example.invalid")]
    [InlineData("GatewayBrowserTransport:DownstreamOrigin", null)]
    [InlineData("GatewayBrowserTransport:DownstreamOrigin", "http://auth.example.invalid")]
    [InlineData("GatewayBrowserTransport:DownstreamOrigin", "https://auth.example.invalid/prefix")]
    [InlineData("GatewayBrowserTransport:DownstreamOrigin", "https://auth.example.invalid/?query=value")]
    [InlineData("GatewayBrowserTransport:DownstreamOrigin", "https://auth.example.invalid/#fragment")]
    [InlineData("GatewayBrowserTransport:ClientPeers:0", null)]
    [InlineData("GatewayBrowserTransport:ClientPeers:0", "invalid")]
    [InlineData("GatewayBrowserTransport:ClientPeers:0", "fe80::1%2")]
    public async Task Invalid_transport_configuration_fails_readiness_and_keeps_liveness(string key, string? value)
    {
        await using var host = await StartAsync(configure: values =>
        {
            if (value is null) values.Remove(key);
            else values[key] = value;
        });
        await AssertHealthAsync(host.Http, "/health/live", HttpStatusCode.OK);
        await AssertHealthAsync(host.Http, "/health/ready", HttpStatusCode.ServiceUnavailable);
        Assert.DoesNotContain(host.Routes.Routes, route => route.Key!.StartsWith("GatewayBrowserTransport:"));
    }

    [Fact]
    public async Task Duplicate_canonical_peers_fail_closed()
    {
        await using var host = await StartAsync(configure: values =>
            values["GatewayBrowserTransport:ClientPeers:1"] = "::ffff:127.0.0.1");
        await AssertHealthAsync(host.Http, "/health/ready", HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData("upstream-path")]
    [InlineData("downstream-path")]
    [InlineData("method")]
    [InlineData("case-insensitive")]
    [InlineData("certificate-bypass")]
    [InlineData("cache")]
    [InlineData("authentication")]
    [InlineData("redirect")]
    [InlineData("cookie-container")]
    [InlineData("tracing")]
    [InlineData("proxy")]
    [InlineData("missing-route")]
    [InlineData("duplicate-route")]
    public async Task Invalid_browser_route_options_fail_readiness_and_remove_only_browser_routes(string mutation)
    {
        await using var host = await StartAsync(mutateRoutes: file =>
        {
            var route = file.Routes[0];
            switch (mutation)
            {
                case "upstream-path": route.UpstreamPathTemplate = "/changed"; break;
                case "downstream-path": route.DownstreamPathTemplate = "/changed"; break;
                case "method": route.UpstreamHttpMethod = ["PUT"]; break;
                case "case-insensitive": route.RouteIsCaseSensitive = false; break;
                case "certificate-bypass": route.DangerousAcceptAnyServerCertificateValidator = true; break;
                case "cache": route.FileCacheOptions.TtlSeconds = 1; break;
                case "authentication": route.AuthenticationOptions.AuthenticationProviderKeys = ["Bearer"]; break;
                case "redirect": route.HttpHandlerOptions.AllowAutoRedirect = true; break;
                case "cookie-container": route.HttpHandlerOptions.UseCookieContainer = true; break;
                case "tracing": route.HttpHandlerOptions.UseTracing = true; break;
                case "proxy": route.HttpHandlerOptions.UseProxy = true; break;
                case "missing-route": file.Routes.RemoveAt(0); break;
                case "duplicate-route": file.Routes.Add(route); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        });
        await AssertHealthAsync(host.Http, "/health/ready", HttpStatusCode.ServiceUnavailable);
        Assert.Equal("legacy", Assert.Single(host.Routes.Routes).Key);
    }

    [Fact]
    public async Task LocalStack_health_does_not_release_HTTP_browser_business_requests()
    {
        await using var host = await StartAsync();
        await AssertHealthAsync(host.Http, "/health/live", HttpStatusCode.OK);
        using var response = await host.Http.GetAsync(CsrfPath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Received);
    }

    [Theory]
    [InlineData(CsrfPath)]
    [InlineData("/api/v1/browser-organisations/eligible")]
    [InlineData("/api/v1/tenant-access/11111111-1111-1111-1111-111111111111")]
    public async Task Valid_HTTPS_browser_transport_forwards_the_canonical_peer_and_existing_credentials(string path)
    {
        await using var host = await StartAsync(configure: values =>
            values["GatewayBrowserTransport:ClientPeers:0"] = "::ffff:127.0.0.1");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-forwarded-value");
        using var response = await host.Https.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var received = Assert.Single(host.Received);
        Assert.Equal(path, received.Path);
        Assert.Equal("127.0.0.1", received.ForwardedFor);
        Assert.Equal("Bearer synthetic-forwarded-value", received.Authorization);
    }

    [Theory]
    [InlineData("query", HttpStatusCode.BadRequest)]
    [InlineData("origin", HttpStatusCode.Forbidden)]
    [InlineData("multiple-origins", HttpStatusCode.Forbidden)]
    [InlineData("forwarded", HttpStatusCode.ServiceUnavailable)]
    [InlineData("x-forwarded", HttpStatusCode.ServiceUnavailable)]
    [InlineData("x-original", HttpStatusCode.ServiceUnavailable)]
    public async Task Browser_admission_rejects_unreviewed_inputs_before_forwarding(string input, HttpStatusCode status)
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, input == "query" ? CsrfPath + "?value=private" : CsrfPath);
        switch (input)
        {
            case "origin": request.Headers.TryAddWithoutValidation("Origin", "https://other.example.invalid"); break;
            case "multiple-origins": request.Headers.TryAddWithoutValidation("Origin", new[] { Origin, Origin }); break;
            case "forwarded": request.Headers.TryAddWithoutValidation("Forwarded", "for=127.0.0.1"); break;
            case "x-forwarded": request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1"); break;
            case "x-original": request.Headers.TryAddWithoutValidation("X-Original-URL", "/private"); break;
        }
        using var response = await host.Https.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Empty(host.Received);
    }

    [Theory]
    [InlineData("POST", "/api/v1/browser-session/csrf")]
    [InlineData("GET", "/api/v1/Browser-session/csrf")]
    [InlineData("GET", "/api/v1/tenant-access/00000000-0000-0000-0000-000000000000")]
    [InlineData("GET", "/api/v1/tenant-access/not-a-guid")]
    [InlineData("GET", "/api/v1/browser-session/csrf%2f")]
    public async Task Only_exact_reviewed_browser_method_paths_are_admitted(string method, string path)
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await host.Https.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(host.Received);
    }

    [Fact]
    public async Task Unapproved_peers_and_downstream_exceptions_are_sanitized()
    {
        await using (var unapproved = await StartAsync(configure: values =>
            values["GatewayBrowserTransport:ClientPeers:0"] = "127.0.0.2"))
        {
            using var denied = await unapproved.Https.GetAsync(CsrfPath);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Empty(unapproved.Received);
        }
        await using var failed = await StartAsync(throwAtDestination: true);
        using var response = await failed.Https.GetAsync(CsrfPath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertHealthAsync(HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Null(response.Content.Headers.ContentType);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static async Task<Host> StartAsync(string environment = "LocalStack",
        Action<Dictionary<string, string?>>? configure = null, Action<FileConfiguration>? mutateRoutes = null,
        Func<HealthStatus>? dependency = null, bool throwAtDestination = false)
    {
        var certificates = new Certificates();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificates.Server));
        });
        var values = Configuration();
        configure?.Invoke(values);
        builder.Configuration.AddInMemoryCollection(values);
        BrowserTransport.Add(builder);
        if (mutateRoutes is not null) builder.Services.Configure(mutateRoutes);
        if (dependency is not null)
        {
            builder.Services.AddHealthChecks().AddCheck("fixture-ready",
                () => new HealthCheckResult(dependency(), "private dependency detail"), tags: ["ready"]);
            builder.Services.AddHealthChecks().AddCheck("fixture-not-ready-tagged",
                () => HealthCheckResult.Unhealthy("private detail that must not change ready"), tags: ["other"]);
        }
        var app = builder.Build();
        var routes = app.Services.GetRequiredService<IOptions<FileConfiguration>>().Value;
        var received = new List<Received>();
        app.UseLocalStackHealthChecks();
        app.UseBrowserTransport();
        app.Run(context =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/v1"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            if (throwAtDestination) throw new InvalidOperationException("private destination details");
            received.Add(new Received(context.Request.Path.Value!, context.Request.Headers["X-Forwarded-For"].ToString(),
                context.Request.Headers.Authorization.ToString()));
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StartAsync(timeout.Token);
            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses;
            var httpAddress = addresses.Single(address => address.StartsWith("http://", StringComparison.Ordinal));
            var tlsPort = new Uri(addresses.Single(address => address.StartsWith("https://", StringComparison.Ordinal))).Port;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    CertificateChainPolicy = new X509ChainPolicy
                    {
                        TrustMode = X509ChainTrustMode.CustomRootTrust,
                        RevocationMode = X509RevocationMode.NoCheck,
                        CustomTrustStore = { certificates.Root }
                    }
                },
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, tlsPort, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            return new Host(app, routes, received, certificates,
                new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
                { BaseAddress = new Uri(httpAddress), Timeout = TimeSpan.FromSeconds(5) },
                new HttpClient(handler) { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(5) });
        }
        catch
        {
            await app.DisposeAsync();
            certificates.Dispose();
            throw;
        }
    }

    private static Dictionary<string, string?> Configuration()
    {
        var values = new Dictionary<string, string?>
        {
            ["GatewayBrowserTransport:Origin"] = Origin,
            ["GatewayBrowserTransport:DownstreamOrigin"] = "https://auth.example.invalid",
            ["GatewayBrowserTransport:ClientPeers:0"] = "127.0.0.1"
        };
        var routes = new[]
        {
            ("GET", CsrfPath), ("POST", "/api/v1/browser-session/sign-in"),
            ("POST", "/api/v1/browser-session/restore"), ("POST", "/api/v1/browser-session/logout"),
            ("GET", "/api/v1/browser-organisations/eligible"), ("GET", "/api/v1/tenant-access/{organisationId}")
        };
        for (var index = 0; index < routes.Length; index++)
        {
            var prefix = $"Routes:{index}:";
            values[prefix + "Key"] = $"GatewayBrowserTransport:{index}";
            values[prefix + "UpstreamPathTemplate"] = routes[index].Item2;
            values[prefix + "DownstreamPathTemplate"] = routes[index].Item2;
            values[prefix + "UpstreamHttpMethod:0"] = routes[index].Item1;
            values[prefix + "RouteIsCaseSensitive"] = "true";
            values[prefix + "FileCacheOptions:TtlSeconds"] = "0";
            values[prefix + "HttpHandlerOptions:AllowAutoRedirect"] = "false";
            values[prefix + "HttpHandlerOptions:UseCookieContainer"] = "false";
            values[prefix + "HttpHandlerOptions:UseTracing"] = "false";
            values[prefix + "HttpHandlerOptions:UseProxy"] = "false";
        }
        values["Routes:6:Key"] = "legacy";
        values["Routes:6:UpstreamPathTemplate"] = "/legacy";
        values["Routes:6:DownstreamPathTemplate"] = "/legacy";
        values["Routes:6:UpstreamHttpMethod:0"] = "GET";
        values["Routes:6:DownstreamScheme"] = "https";
        values["Routes:6:DownstreamHostAndPorts:0:Host"] = "legacy.example.invalid";
        values["Routes:6:DownstreamHostAndPorts:0:Port"] = "443";
        return values;
    }

    private sealed record Received(string Path, string ForwardedFor, string Authorization);

    private sealed record Host(WebApplication App, FileConfiguration Routes, List<Received> Received,
        Certificates Certificates, HttpClient Http, HttpClient Https) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            Https.Dispose();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await App.StopAsync(timeout.Token);
            }
            finally
            {
                await App.DisposeAsync();
                Certificates.Dispose();
            }
        }
    }

    private sealed class Certificates : IDisposable
    {
        private readonly RSA rootKey = RSA.Create(2048);
        private readonly RSA serverKey = RSA.Create(2048);
        internal X509Certificate2 Root { get; }
        internal X509Certificate2 Server { get; }

        internal Certificates()
        {
            var now = DateTimeOffset.UtcNow;
            var root = new CertificateRequest("CN=W1 gateway test root", rootKey, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            root.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            root.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            Root = root.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
            var leaf = new CertificateRequest("CN=browser.example.invalid", serverKey, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("browser.example.invalid");
            leaf.CertificateExtensions.Add(names.Build());
            using var publicLeaf = leaf.Create(Root, now.AddMinutes(-1), now.AddMinutes(30),
                RandomNumberGenerator.GetBytes(16));
            using var keyedLeaf = publicLeaf.CopyWithPrivateKey(serverKey);
            // Schannel needs an imported key handle rather than the original ephemeral RSA key.
            // DefaultKeySet is temporary (no PersistKeySet); fixture disposal owns the certificate.
            var encoded = keyedLeaf.Export(X509ContentType.Pkcs12);
            try { Server = new X509Certificate2(encoded, (string?)null, X509KeyStorageFlags.DefaultKeySet); }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }

        public void Dispose()
        {
            Server.Dispose();
            Root.Dispose();
            serverKey.Dispose();
            rootKey.Dispose();
        }
    }
}

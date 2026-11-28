using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ocelot.Configuration.File;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Zeka.Extensions.Authentication;
using Zeka.Gateway;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
    });
});

builder.Host.ConfigureAppConfiguration((env, config) =>
{
    config.AddJsonFile("ocelot.json", true, true);
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
Zeka.Gateway.BrowserTransport.Add(builder);
builder.Services.AddJwtAuthentication(builder.Configuration);

var app = builder.Build();
app.UseLocalStackHealthChecks();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseBrowserTransport();
app.UseRouting();
app.UseCors("CorsPolicy");
app.UseAuthorization();

app.MapControllers();

app.UseEndpoints(endpoints =>
{
    endpoints.MapGet("/", async context => { await context.Response.WriteAsync("Hello Ocelot"); });
});

app.UseJwtAuthentication();

await app.UseOcelot();
await app.RunAsync();

namespace Zeka.Gateway
{
    // The isolated single-hop profile is opt-in. It is not a production ingress topology.
    public static class BrowserTransport
    {
        public const string DiagnosticCategory = "Zeka.Gateway.Safe";
        private const string Prefix = "GatewayBrowserTransport";
        private const string BeneficiariesPath = "/api/v1/beneficiaries/";
        private const string IdentitySuffix = "/identity";
        private const string OrganisationHeader = "X-Organisation-Id";
        private static readonly (string Method, string Path)[] ClientRoutes =
        [
            ("POST", BeneficiariesPath + "lookup"),
            ("GET", BeneficiariesPath + "{beneficiaryId}" + IdentitySuffix)
        ];
        private static readonly (string Method, string Path)[] Routes =
        [
            ("GET", "/api/v1/browser-session/csrf"),
            ("POST", "/api/v1/browser-session/sign-in"),
            ("POST", "/api/v1/browser-session/restore"),
            ("POST", "/api/v1/browser-session/logout"),
            ("POST", "/api/v1/browser-session/onboarding/registrations"),
            ("POST", "/api/v1/browser-session/onboarding/registrations/resend"),
            ("POST", "/api/v1/browser-session/onboarding/registrations/email-verification/exchange"),
            ("GET", "/api/v1/browser-organisations/eligible"),
            ("POST", "/api/v1/browser-session/organisations"),
            ("GET", "/api/v1/browser-organisations/{organisationId}/access"),
            ("GET", "/api/v1/tenant-access/{organisationId}")
        ];

        public static void Add(WebApplicationBuilder builder)
        {
            var policy = new Policy(builder.Configuration);
            builder.Services.AddSingleton(policy);
            if (builder.Environment.IsEnvironment("LocalStack"))
                builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                    "gateway-browser-transport",
                    services => new LocalStackReadiness(services.GetRequiredService<Policy>()),
                    HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(2)));
            builder.Services.AddCors(options => options.AddPolicy(Prefix, cors =>
            {
                if (policy.Valid) cors.WithOrigins(policy.Origin!).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }));
            // Raw framework/dependency categories include URLs and exception contents.
            // Gateway diagnostics have one source allowlist and a route-aware producer below.
            builder.Services.PostConfigure<LoggerFilterOptions>(options =>
            {
                options.Rules.Clear();
                options.Rules.Add(new LoggerFilterRule(null, null, LogLevel.Information,
                    (_, category, _) => category == DiagnosticCategory));
            });
            builder.Services.AddOcelot(builder.Configuration);
            builder.Services.PostConfigure<FileConfiguration>(file =>
            {
                var browser = file.Routes.Where(route => route.Key?.StartsWith(Prefix + ":", StringComparison.Ordinal) == true).ToArray();
                var valid = policy.Valid && browser.Length == Routes.Length && Routes.All(expected => browser.Count(route =>
                    route.UpstreamPathTemplate == expected.Path && route.DownstreamPathTemplate == expected.Path
                    && route.UpstreamHttpMethod.SequenceEqual(new[] { expected.Method }) && route.RouteIsCaseSensitive
                    && !route.DangerousAcceptAnyServerCertificateValidator && route.FileCacheOptions.TtlSeconds == 0
                    && !route.AuthenticationOptions.AuthenticationProviderKeys.Any()
                    && !route.HttpHandlerOptions.AllowAutoRedirect && !route.HttpHandlerOptions.UseCookieContainer
                    && !route.HttpHandlerOptions.UseTracing && !route.HttpHandlerOptions.UseProxy) == 1);
                policy.Valid = valid;
                foreach (var route in browser)
                {
                    if (!valid) { file.Routes.Remove(route); continue; }
                    route.DownstreamScheme = policy.Target!.Scheme;
                    route.DownstreamHostAndPorts = [new() { Host = policy.Target.Host, Port = policy.Target.Port }];
                }
                ConfigureClientRoutes(file, policy);
            });
        }

        private static void ConfigureClientRoutes(FileConfiguration file, Policy policy)
        {
            policy.ClientValid = false;
            if (!policy.Valid || policy.ClientTarget is not { } target
                || file.Routes.Any(route => route.Priority == int.MaxValue
                    || route.UpstreamPathTemplate?.StartsWith(BeneficiariesPath, StringComparison.OrdinalIgnoreCase) == true))
                return;

            // Only the two reviewed Client routes can use this authority. JSON routes cannot retarget them.
            foreach (var route in ClientRoutes)
            {
                file.Routes.Add(new FileRoute
                {
                    Key = "GatewayClientTransport:" + route.Method,
                    UpstreamPathTemplate = route.Path,
                    DownstreamPathTemplate = route.Path,
                    UpstreamHttpMethod = [route.Method],
                    RouteIsCaseSensitive = true,
                    Priority = int.MaxValue,
                    DownstreamScheme = target.Scheme,
                    DownstreamHostAndPorts = [new() { Host = target.Host, Port = target.Port }],
                    DangerousAcceptAnyServerCertificateValidator = false,
                    FileCacheOptions = new() { TtlSeconds = 0 },
                    HttpHandlerOptions = new()
                    {
                        AllowAutoRedirect = false,
                        UseCookieContainer = false,
                        UseTracing = false,
                        UseProxy = false
                    }
                });
            }
            policy.ClientValid = true;
        }

        public static void UseLocalStackHealthChecks(this WebApplication app)
        {
            if (!app.Environment.IsEnvironment("LocalStack")) return;
            app.UseHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false,
                ResponseWriter = (_, _) => Task.CompletedTask
            });
            app.UseHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready"),
                ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable },
                ResponseWriter = (_, _) => Task.CompletedTask
            });
        }

        private sealed class LocalStackReadiness(Policy policy) : IHealthCheck
        {
            public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                CancellationToken cancellationToken = default) => Task.FromResult(policy.Valid && (!policy.ClientConfigured || policy.ClientValid)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("Gateway transport unavailable."));
        }

        public static void UseBrowserTransport(this WebApplication app)
        {
            var policy = app.Services.GetRequiredService<Policy>();
            var diagnostic = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(DiagnosticCategory);
            app.Use(async (http, next) =>
            {
                var path = http.Request.Path.Value ?? "";
                var routeClass = path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase) ? "browser"
                    : path.StartsWith("/clients/", StringComparison.Ordinal) ? "legacy-clients"
                    : path == "/login" ? "legacy-login" : "other";
                var method = http.Request.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
                    ? http.Request.Method : "other";
                var correlation = Guid.NewGuid().ToString("N"); // Never trust a caller's correlation value.
                var failed = false;
                try { await next(http); }
                catch { failed = true; throw; }
                finally
                {
                    diagnostic.LogInformation(new EventId(8901, "GatewayRequest"),
                        "GatewayRequest route={RouteClass} method={MethodClass} status={Status} outcome={Outcome} dependency={DependencyClass} correlation={Correlation}",
                        routeClass, method, http.Response.StatusCode, failed ? "exception" : "completed",
                        failed || http.Response.StatusCode >= 500 ? "unavailable" : "none", correlation);
                }
            });
            app.UseWhen(context => context.Request.Path.Value?.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase) == true,
                branch =>
                {
                    branch.Use(async (http, next) =>
                    {
                        http.Response.OnStarting(() =>
                        {
                            http.Response.Headers.CacheControl = "no-store";
                            if (http.Response.StatusCode >= 500) http.Response.StatusCode = 503;
                            return Task.CompletedTask;
                        });
                        var path = http.Request.Path.Value!;
                        var clientRoute = policy.ClientConfigured && ClientRoutes.Any(route =>
                            route.Method == http.Request.Method && MatchesRoute(route.Path, path));
                        var browserRoute = Routes.Any(route => route.Method == http.Request.Method &&
                            MatchesRoute(route.Path, path));
                        if (!(browserRoute || clientRoute)
                            || http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget?.Contains('%') == true)
                        { http.Response.StatusCode = 404; return; }
                        if (!(clientRoute ? policy.ClientValid : policy.Valid) || !http.Request.IsHttps
                            || http.Request.Scheme + "://" + http.Request.Host != policy.Origin)
                        { http.Response.StatusCode = 503; return; }
                        var forwarding = http.Request.Headers.Keys.Where(name => name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)).ToArray();
                        foreach (var name in forwarding) http.Request.Headers.Remove(name);
                        var peer = Canonical(http.Connection.RemoteIpAddress);
                        if (forwarding.Length != 0 || peer is null || !policy.Peers.Contains(peer))
                        { http.Response.StatusCode = 503; return; }
                        if (http.Request.QueryString.HasValue) { http.Response.StatusCode = 400; return; }
                        if (http.Request.Headers.Origin.Count > 0 && (http.Request.Headers.Origin.Count != 1
                            || http.Request.Headers.Origin[0] != policy.Origin))
                        { http.Response.StatusCode = 403; return; }
                        if (clientRoute)
                        {
                            if (ClientHeaderFailure(http.Request) is { } status)
                            { http.Response.StatusCode = status; return; }
                            http.Request.Headers.Remove("Cookie");
                        }
                        else
                        {
                            http.Request.Headers["X-Forwarded-For"] = peer;
                        }
                        try { await next(http); }
                        catch (Exception) when (!http.Response.HasStarted)
                        { http.Response.Clear(); http.Response.StatusCode = 503; http.Response.Headers.CacheControl = "no-store"; }
                    });
                    branch.UseCors(Prefix);
                });
        }

        private static int? ClientHeaderFailure(HttpRequest request)
        {
            var authorization = request.Headers.Authorization;
            if (authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(authorization[0], out var bearer)
                || !bearer.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(bearer.Parameter))
                return StatusCodes.Status401Unauthorized;

            var organisation = request.Headers[OrganisationHeader];
            return organisation.Count == 1 && Guid.TryParseExact(organisation[0], "D", out var id) && id != Guid.Empty
                ? null : StatusCodes.Status403Forbidden;
        }

        private static bool MatchesRoute(string template, string path) => template switch
        {
            "/api/v1/tenant-access/{organisationId}" => GuidSegment(path, "/api/v1/tenant-access/", ""),
            "/api/v1/browser-organisations/{organisationId}/access" => GuidSegment(path, "/api/v1/browser-organisations/", "/access"),
            BeneficiariesPath + "{beneficiaryId}" + IdentitySuffix => BeneficiaryIdentityPath(path),
            _ => template == path
        };

        private static bool GuidSegment(string path, string prefix, string suffix)
            => path.Length == prefix.Length + 36 + suffix.Length
                && path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal)
                && Guid.TryParseExact(path.Substring(prefix.Length, 36), "D", out var id) && id != Guid.Empty;

        private static bool BeneficiaryIdentityPath(string path)
        {
            if (path.Length <= BeneficiariesPath.Length + IdentitySuffix.Length
                || !path.StartsWith(BeneficiariesPath, StringComparison.Ordinal)
                || !path.EndsWith(IdentitySuffix, StringComparison.Ordinal)) return false;
            var segment = path.AsSpan(BeneficiariesPath.Length, path.Length - BeneficiariesPath.Length - IdentitySuffix.Length);
            return segment[0] != '0'
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
        }

        private static string? Canonical(System.Net.IPAddress? address) => address?.AddressFamily is
            System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : null;

        private sealed class Policy
        {
            internal bool Valid { get; set; }
            internal bool ClientConfigured { get; }
            internal bool ClientValid { get; set; }
            internal Uri? ClientTarget { get; }
            internal string? Origin { get; }
            internal Uri? Target { get; }
            internal HashSet<string> Peers { get; } = new(StringComparer.Ordinal);
            internal Policy(IConfiguration configuration)
            {
                Origin = configuration[Prefix + ":Origin"];
                var clientOrigin = configuration[Prefix + ":ClientDownstreamOrigin"];
                ClientConfigured = clientOrigin is not null;
                if (!Uri.TryCreate(Origin, UriKind.Absolute, out var origin) || origin.Scheme != "https"
                    || Origin != origin.GetLeftPart(UriPartial.Authority) || origin.UserInfo.Length != 0
                    || !Uri.TryCreate(configuration[Prefix + ":DownstreamOrigin"], UriKind.Absolute, out var target)
                    || target.Scheme != "https" || target.AbsolutePath != "/" || target.UserInfo.Length != 0
                    || target.Query.Length != 0 || target.Fragment.Length != 0) return;
                Target = target;
                if (Uri.TryCreate(clientOrigin, UriKind.Absolute, out var client) && client.Scheme == "https"
                    && client.AbsolutePath == "/" && client.UserInfo.Length == 0 && client.Query.Length == 0
                    && client.Fragment.Length == 0 && client != target && client != origin)
                    ClientTarget = client;
                foreach (var raw in configuration.GetSection(Prefix + ":ClientPeers").Get<string[]>() ?? [])
                {
                    if (raw.Contains('%') || !System.Net.IPAddress.TryParse(raw, out var address)
                        || Canonical(address) is not { } peer || !Peers.Add(peer)) return;
                }
                Valid = Peers.Count > 0;
            }
        }
    }
}

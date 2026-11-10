using System.Globalization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        private const string TenantAccessPrefix = "/api/v1/tenant-access/";
        private const string TenantAccessPath = TenantAccessPrefix + "{organisationId}";
        private const string BeneficiaryPrefix = "/api/v1/beneficiaries/";
        private const string InteractionSuffix = "/interactions";
        private const string LinkedInteractionPath = BeneficiaryPrefix + "{beneficiaryId}" + InteractionSuffix;

        private enum Destination { Auth, Client }
        private sealed record Operation(string Method, string Path, Destination Destination);

        private static readonly Operation[] Routes =
        [
            new("GET", "/api/v1/browser-session/csrf", Destination.Auth),
            new("POST", "/api/v1/browser-session/sign-in", Destination.Auth),
            new("POST", "/api/v1/browser-session/restore", Destination.Auth),
            new("POST", "/api/v1/browser-session/logout", Destination.Auth),
            new("GET", "/api/v1/browser-organisations/eligible", Destination.Auth),
            new("GET", TenantAccessPath, Destination.Auth),
            new("POST", LinkedInteractionPath, Destination.Client)
        ];

        public static void Add(WebApplicationBuilder builder)
        {
            var policy = new Policy(builder.Configuration, builder.Environment.IsEnvironment("LocalStack"));
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
            builder.Services.PostConfigure<Ocelot.Configuration.File.FileConfiguration>(file =>
            {
                var browser = file.Routes.Where(route => route.Key?.StartsWith(Prefix + ":", StringComparison.Ordinal) == true).ToArray();
                var valid = policy.Valid && browser.Length == policy.Operations.Length
                    && policy.Operations.All(expected => browser.Count(route =>
                    route.UpstreamPathTemplate == expected.Path && route.DownstreamPathTemplate == expected.Path
                    && route.UpstreamHttpMethod.SequenceEqual(new[] { expected.Method }) && route.RouteIsCaseSensitive
                    && !route.DangerousAcceptAnyServerCertificateValidator && route.FileCacheOptions.TtlSeconds == 0
                    && !route.AuthenticationOptions.AuthenticationProviderKeys.Any()
                    && !route.HttpHandlerOptions.AllowAutoRedirect && !route.HttpHandlerOptions.UseCookieContainer
                    && !route.HttpHandlerOptions.UseTracing && !route.HttpHandlerOptions.UseProxy
                    && (expected.Destination == Destination.Auth
                        || (route.DownstreamScheme == "http" && route.DownstreamHostAndPorts.Count == 1
                            && route.DownstreamHostAndPorts[0].Host == "client"
                            && route.DownstreamHostAndPorts[0].Port == 8080))) == 1);
                policy.Valid = valid;
                foreach (var route in browser)
                {
                    if (!valid) { file.Routes.Remove(route); continue; }
                    var operation = policy.Operations.Single(expected => expected.Path == route.UpstreamPathTemplate);
                    if (operation.Destination == Destination.Client) continue;

                    route.DownstreamScheme = policy.Target!.Scheme;
                    route.DownstreamHostAndPorts = [new() { Host = policy.Target.Host, Port = policy.Target.Port }];
                }
            });
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
                CancellationToken cancellationToken = default) => Task.FromResult(policy.Valid
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
                        var rawTarget = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
                        var operation = policy.Operations.SingleOrDefault(candidate => candidate.Method == http.Request.Method
                            && MatchesPath(candidate.Path, path, rawTarget));
                        if (operation is null || (operation.Destination == Destination.Auth && rawTarget?.Contains('%') == true))
                        { http.Response.StatusCode = 404; return; }
                        if (!policy.Valid || !http.Request.IsHttps || http.Request.Scheme + "://" + http.Request.Host != policy.Origin)
                        { http.Response.StatusCode = 503; return; }
                        var forwarding = http.Request.Headers.Keys.Where(name => name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)).ToArray();
                        foreach (var name in forwarding) http.Request.Headers.Remove(name);
                        var peer = Canonical(http.Connection.RemoteIpAddress);
                        if (forwarding.Length != 0 || peer is null || !policy.Peers.Contains(peer))
                        { http.Response.StatusCode = 503; return; }
                        // The Client wire contract distinguishes linked and interaction-only requests.
                        if (operation.Destination == Destination.Auth && http.Request.QueryString.HasValue)
                        { http.Response.StatusCode = 400; return; }
                        if (http.Request.Headers.Origin.Count > 0 && (http.Request.Headers.Origin.Count != 1
                            || http.Request.Headers.Origin[0] != policy.Origin))
                        { http.Response.StatusCode = 403; return; }
                        http.Request.Headers["X-Forwarded-For"] = peer;
                        try { await next(http); }
                        catch (Exception) when (!http.Response.HasStarted)
                        { http.Response.Clear(); http.Response.StatusCode = 503; http.Response.Headers.CacheControl = "no-store"; }
                    });
                    branch.UseCors(Prefix);
                });
        }

        private static bool MatchesPath(string template, string path, string? rawTarget)
        {
            if (template == TenantAccessPath)
                return path.StartsWith(TenantAccessPrefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(path.AsSpan(TenantAccessPrefix.Length), "D", out var organisationId)
                    && organisationId != Guid.Empty;
            if (template != LinkedInteractionPath) return template == path;

            if (!path.StartsWith(BeneficiaryPrefix, StringComparison.Ordinal)
                || !path.EndsWith(InteractionSuffix, StringComparison.Ordinal)
                || path.Length <= BeneficiaryPrefix.Length + InteractionSuffix.Length
                || rawTarget?.Split('?', 2)[0] != path) return false;

            var id = path.AsSpan(BeneficiaryPrefix.Length, path.Length - BeneficiaryPrefix.Length - InteractionSuffix.Length);
            return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var beneficiaryId)
                && beneficiaryId > 0
                && id.SequenceEqual(beneficiaryId.ToString(CultureInfo.InvariantCulture).AsSpan());
        }

        private static string? Canonical(System.Net.IPAddress? address) => address?.AddressFamily is
            System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : null;

        private sealed class Policy
        {
            internal bool Valid { get; set; }
            internal string? Origin { get; }
            internal Uri? Target { get; }
            internal HashSet<string> Peers { get; } = new(StringComparer.Ordinal);
            internal Operation[] Operations { get; }

            internal Policy(IConfiguration configuration, bool localStack)
            {
                // The packaged profile keeps the six Auth routes. LocalStack mounts the tracked seventh route.
                Operations = Routes.Where(operation => localStack || operation.Destination == Destination.Auth).ToArray();
                Origin = configuration[Prefix + ":Origin"];
                if (!Uri.TryCreate(Origin, UriKind.Absolute, out var origin) || origin.Scheme != "https"
                    || Origin != origin.GetLeftPart(UriPartial.Authority) || origin.UserInfo.Length != 0
                    || !Uri.TryCreate(configuration[Prefix + ":DownstreamOrigin"], UriKind.Absolute, out var target)
                    || target.Scheme != "https" || target.AbsolutePath != "/" || target.UserInfo.Length != 0
                    || target.Query.Length != 0 || target.Fragment.Length != 0) return;
                Target = target;
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
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
        private const string Prefix = "GatewayBrowserTransport";
        private static readonly (string Method, string Path)[] Routes =
        [
            ("GET", "/api/v1/browser-session/csrf"),
            ("POST", "/api/v1/browser-session/sign-in"),
            ("POST", "/api/v1/browser-session/restore"),
            ("POST", "/api/v1/browser-session/logout"),
            ("GET", "/api/v1/browser-organisations/eligible"),
            ("GET", "/api/v1/tenant-access/{organisationId}")
        ];

        public static void Add(WebApplicationBuilder builder)
        {
            var policy = new Policy(builder.Configuration);
            builder.Services.AddSingleton(policy);
            builder.Services.AddCors(options => options.AddPolicy(Prefix, cors =>
            {
                if (policy.Valid) cors.WithOrigins(policy.Origin!).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }));
            // Ocelot logs URLs and errors. Browser credentials/IDs must not enter those logs.
            builder.Logging.AddFilter("Ocelot", LogLevel.None);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
            builder.Services.AddOcelot(builder.Configuration);
            builder.Services.PostConfigure<Ocelot.Configuration.File.FileConfiguration>(file =>
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
            });
        }

        public static void UseBrowserTransport(this WebApplication app)
        {
            var policy = app.Services.GetRequiredService<Policy>();
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
                        var selection = path.StartsWith("/api/v1/tenant-access/", StringComparison.Ordinal)
                            && Guid.TryParseExact(path["/api/v1/tenant-access/".Length..], "D", out var id) && id != Guid.Empty;
                        var exact = Routes.Any(route => route.Method == http.Request.Method &&
                            (route.Path == path || (route.Path == Routes[^1].Path && selection)));
                        if (!exact || http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget?.Contains('%') == true)
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
                        if (http.Request.QueryString.HasValue) { http.Response.StatusCode = 400; return; }
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

        private static string? Canonical(System.Net.IPAddress? address) => address?.AddressFamily is
            System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : null;

        private sealed class Policy
        {
            internal bool Valid { get; set; }
            internal string? Origin { get; }
            internal Uri? Target { get; }
            internal HashSet<string> Peers { get; } = new(StringComparer.Ordinal);
            internal Policy(IConfiguration configuration)
            {
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

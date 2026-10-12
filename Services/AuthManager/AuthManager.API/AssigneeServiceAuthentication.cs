using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zeka.Authentication;

namespace AuthManager.API;

/// <summary>Purpose-specific ClientManagement workload authentication, independent of user Bearer authority.</summary>
public static class AssigneeServiceAuthentication
{
    public const string Scheme = "AssigneeEligibilityService.v1";

    public static IServiceCollection AddAssigneeServiceAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var settings = AssigneeServiceOptions.Read(configuration);
        services.AddSingleton(settings);
        services.AddAuthentication().AddJwtBearer(Scheme);
        services.AddAuthorization();

        if (!settings.Enabled)
        {
            // The capability remains inaccessible until a separately authorised platform configuration exists.
            services.AddOptions<JwtBearerOptions>(Scheme).Configure(options =>
            {
                options.IncludeErrorDetails = false;
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context => { context.Fail("Service identity disabled."); return Task.CompletedTask; },
                    OnChallenge = NonDisclosingChallenge
                };
            });
            return services;
        }

        services.AddHttpClient<AssigneeJwksSource>(http =>
            { http.Timeout = TimeSpan.FromSeconds(2); }).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<AssigneeJwksTrust>();
        services.AddOptions<JwtBearerOptions>(Scheme).Configure<AssigneeJwksTrust>((options, trust) =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.RefreshOnIssuerKeyNotFound = false;
            options.TokenHandlers.Clear();
            options.TokenHandlers.Add(new JwksTokenHandler(trust, settings.Validation));
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = settings.Issuer,
                ValidateAudience = true, ValidAudience = settings.Audience,
                ValidateLifetime = true, RequireExpirationTime = true,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], TryAllIssuerSigningKeys = false,
                ClockSkew = TimeSpan.FromSeconds(30), LogValidationExceptions = false,
                IncludeTokenOnFailedValidation = false
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var claims = context.Principal?.Claims.ToArray() ?? [];
                    var subjects = claims.Where(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "sub")
                        .Select(c => c.Value).ToArray();
                    var issued = claims.Where(c => c.Type == "iat").Select(c => c.Value).ToArray();
                    var expires = claims.Where(c => c.Type == "exp").Select(c => c.Value).ToArray();
                    var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>()
                        .GetUtcNow().ToUnixTimeSeconds();
                    if (subjects.Length != 1 || subjects[0] != settings.ClientManagementSubject
                        || issued.Length != 1 || expires.Length != 1
                        || !long.TryParse(issued[0], NumberStyles.None, CultureInfo.InvariantCulture, out var iat)
                        || !long.TryParse(expires[0], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)
                        || iat > now + 30 || exp <= iat || exp - iat > 600)
                        context.Fail("Invalid service credential.");
                    return Task.CompletedTask;
                },
                OnChallenge = NonDisclosingChallenge,
                OnAuthenticationFailed = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("AuthManager.AssigneeServiceAuthentication")
                        .LogWarning("Assignee service authentication failed: {Outcome}.",
                            context.Exception is AuthenticationAuthorityUnavailableException ? "unavailable" : "invalid");
                    return Task.CompletedTask;
                }
            };
        });
        return services;
    }

    private static Task NonDisclosingChallenge(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.StatusCode = context.AuthenticateFailure is AuthenticationAuthorityUnavailableException
            ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
        if (context.Response.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

public sealed record AssigneeServiceOptions
{
    public bool Enabled { get; init; }
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    public string JwksUri { get; init; } = "";
    public string ClientManagementSubject { get; init; } = "";
    public AuthenticationOptions Validation => new()
    {
        Issuer = Issuer, Audience = Audience, JwksUri = JwksUri,
        ClockSkew = TimeSpan.FromSeconds(30),
        JwksRefreshInterval = TimeSpan.FromSeconds(10),
        JwksMaximumStaleness = TimeSpan.FromSeconds(600)
    };

    public static AssigneeServiceOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("AssigneeServiceAuthentication");
        var allowed = new[] { nameof(Enabled), nameof(Issuer), nameof(Audience), nameof(JwksUri),
            nameof(ClientManagementSubject) };
        if (section.GetChildren().Any(child => !allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase)
            || child.GetChildren().Any()))
            throw new InvalidOperationException("Invalid assignee service authentication configuration.");
        var result = section.Get<AssigneeServiceOptions>() ?? new();
        if (!result.Enabled) return result;
        if (!AuthenticationOptions.Https(result.Issuer) || !AuthenticationOptions.Https(result.JwksUri)
            || string.IsNullOrWhiteSpace(result.Audience) || result.Audience != result.Audience.Trim()
            || result.Audience.Length > 256 || result.Audience.Any(char.IsWhiteSpace)
            || result.Audience.Contains(',') || result.Audience == configuration["Authentication:Audience"]
            || !Regex.IsMatch(result.ClientManagementSubject,
                @"^system:serviceaccount:[a-z0-9]([-a-z0-9]*[a-z0-9])?:[a-z0-9]([-a-z0-9]*[a-z0-9])?$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new InvalidOperationException("Invalid assignee service authentication configuration.");
        result.Validation.Validate();
        return result;
    }
}

public sealed class AssigneeJwksSource(HttpClient http, AssigneeServiceOptions options) : IJwksDocumentSource
{
    private readonly HttpJwksDocumentSource source = new(http, options.Validation);
    public Task<string> ReadAsync(CancellationToken cancellationToken) => source.ReadAsync(cancellationToken);
}

public sealed class AssigneeJwksTrust(AssigneeJwksSource source, AssigneeServiceOptions options, TimeProvider clock) : IJwksTrust
{
    private readonly JwksTrust trust = new(source, options.Validation, clock);
    public Task<SecurityKey> ResolveAsync(string kid, CancellationToken cancellationToken = default)
        => trust.ResolveAsync(kid, cancellationToken);
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        => trust.IsReadyAsync(cancellationToken);
}

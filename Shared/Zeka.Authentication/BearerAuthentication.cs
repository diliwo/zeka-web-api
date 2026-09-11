using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
namespace Zeka.Authentication;

public sealed class JwksTokenHandler(IJwksTrust trust, AuthenticationOptions settings) : JsonWebTokenHandler
{
    public const string DiagnosticsName = "Zeka.Authentication";
    private static readonly Meter Metrics = new(DiagnosticsName);
    private static readonly Counter<long> Outcomes = Metrics.CreateCounter<long>("authentication.outcomes");
    private static readonly ActivitySource Traces = new(DiagnosticsName);

    public override async Task<TokenValidationResult> ValidateTokenAsync(string token, TokenValidationParameters validationParameters)
    {
        using var trace = Traces.StartActivity("authentication.validate");
        var outcome = "invalid";
        try
        {
            // Read only to select a trusted key. Framework validation authenticates all token fields below.
            if (string.IsNullOrEmpty(token) || token.Length > 16384 || token.Count(c => c == '.') != 2)
                throw new InvalidBearerTokenException();
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[0]));
            var fields = header.RootElement.EnumerateObject().ToArray();
            if (!header.RootElement.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String
                || fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
                || fields.Any(x => x.Name is "jku" or "jwk" or "x5u" or "crit"))
                throw new InvalidBearerTokenException();
            var parsed = ReadJsonWebToken(token);
            if (parsed.Alg != SecurityAlgorithms.RsaSha256 || string.IsNullOrWhiteSpace(parsed.Kid) || parsed.Kid.Length > 128
                || parsed.Issuer != settings.Issuer || parsed.Audiences.Count() != 1 || parsed.Audiences.Single() != settings.Audience)
                throw new InvalidBearerTokenException();
            var key = await trust.ResolveAsync(parsed.Kid);
            var parameters = validationParameters.Clone();
            parameters.IssuerSigningKey = key;
            parameters.IssuerSigningKeys = null;
            parameters.TryAllIssuerSigningKeys = false;
            var result = await base.ValidateTokenAsync(token, parameters);
            if (!result.IsValid) throw new InvalidBearerTokenException();
            outcome = "authenticated";
            return result;
        }
        catch (AuthenticationAuthorityUnavailableException)
        { outcome = "unavailable"; return new TokenValidationResult { IsValid = false, Exception = new AuthenticationAuthorityUnavailableException() }; }
        catch (Exception)
        { return new TokenValidationResult { IsValid = false, Exception = new InvalidBearerTokenException() }; }
        finally
        {
            trace?.SetTag("outcome", outcome);
            Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}

public static class BearerAuthentication
{
    public static IServiceCollection AddZekaBearerValidation(this IServiceCollection services, IConfiguration configuration, bool issuerHost = false)
    {
        var settings = AuthenticationOptions.Read(configuration);
        if (!issuerHost && configuration.GetSection("AuthenticationIssuer").GetChildren().Any())
            throw new InvalidOperationException("Issuer configuration is forbidden in validator-only services.");
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<IJwksDocumentSource, HttpJwksDocumentSource>(http =>
        { http.Timeout = AuthenticationOptions.RequestTimeout; }).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.TryAddSingleton<IJwksTrust, JwksTrust>();
        services.AddHealthChecks().AddCheck<JwksReadinessCheck>("authentication-jwks", tags: ["ready"]);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IJwksTrust>((options, trust) =>
        {
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.RefreshOnIssuerKeyNotFound = false;
            options.TokenHandlers.Clear();
            options.TokenHandlers.Add(new JwksTokenHandler(trust, settings));
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = settings.Issuer,
                ValidateAudience = true, ValidAudience = settings.Audience,
                ValidateLifetime = true, RequireExpirationTime = true,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                TryAllIssuerSigningKeys = false, ClockSkew = settings.ClockSkew,
                LogValidationExceptions = false, IncludeTokenOnFailedValidation = false
            };
            options.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = context.AuthenticateFailure is AuthenticationAuthorityUnavailableException ? 503 : 401;
                    context.Response.Headers.CacheControl = "no-store";
                    if (context.Response.StatusCode == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Zeka.Authentication")
                        .LogWarning("Bearer authentication failed: {Outcome}.",
                            context.Exception is AuthenticationAuthorityUnavailableException ? "unavailable" : "invalid");
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization();
        return services;
    }
}

public sealed class JwksReadinessCheck(IJwksTrust trust) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => await trust.IsReadyAsync(cancellationToken) ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Authentication authority unavailable.");
}

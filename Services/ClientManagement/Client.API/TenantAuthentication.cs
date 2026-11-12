using ClientManagement.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zeka.Authentication;

namespace ClientManagement.API;

public static class TenantAuthentication
{
    public static IServiceCollection AddTenantAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddZekaBearerValidation(configuration);
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var onChallenge = options.Events.OnChallenge;
            options.Events.OnChallenge = async challenge =>
            {
                var http = challenge.HttpContext;
                var requestCorrelationId = http.TraceIdentifier;
                await onChallenge(challenge);

                if (challenge.AuthenticateFailure is AuthenticationAuthorityUnavailableException &&
                    challenge.Response.StatusCode == StatusCodes.Status503ServiceUnavailable)
                {
                    AuthDependencyUnavailableLog.Write(http, requestCorrelationId, () => http.RequestServices
                        .GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TenantAuthentication).FullName!));
                }
            };
        });
        return services;
    }
}

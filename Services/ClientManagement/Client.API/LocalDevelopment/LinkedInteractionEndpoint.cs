using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.BeneficiaryB1;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace ClientManagement.API.LocalDevelopment;

public static class LinkedInteractionEndpoint
{
    private const string Flag = "LocalDevelopment:LinkedInteractionEnabled";
    private const string AuthorityUrl = "ActionAuthority:AuthManagementUrl";

    public static bool AddLocalLinkedInteraction(this WebApplicationBuilder builder)
    {
        var setting = builder.Configuration[Flag];
        var parsed = bool.TryParse(setting, out var enabled);
        var allowed = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("LocalStack");
        builder.Services.AddOptions<ActivationOptions>()
            .Validate(_ => setting is null || parsed, "The linked interaction flag must be a boolean.")
            .Validate(_ => !enabled || allowed, "Linked interactions require Development or LocalStack.")
            .ValidateOnStart();

        if (!enabled || !allowed) return false;

        // Bind the credential and the existing HTTP client to the same startup-only authority origin.
        var authority = builder.Configuration[AuthorityUrl];
        var tokenFile = builder.Configuration["LocalDevelopment:ActionAuthorityTokenFile"];
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [AuthorityUrl] = authority });
        builder.Services.AddScoped<LinkedWriteGate>();
        builder.Services.AddScoped<IActionWriteActivationGate>(sp => sp.GetRequiredService<LinkedWriteGate>());
        builder.Services.AddScoped<IActionAuthorityServiceCredential>(sp =>
            new LocalActionAuthorityCredential(sp.GetRequiredService<LinkedWriteGate>(), authority, tokenFile));
        return true;
    }

    public static void MapLocalLinkedInteraction(this WebApplication app)
    {
        app.MapPost("/api/v1/beneficiaries/{beneficiaryId}/interactions", RecordAsync)
            .WithMetadata(new LocalLinkedWrite())
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme });
    }

    private static async Task RecordAsync(HttpContext http, string beneficiaryId, BeneficiaryB1Service service,
        LinkedWriteGate gate)
    {
        http.Response.OnStarting(() =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        gate.Active = true;
        try
        {
            var result = await BeneficiaryInteractionWire.RecordAsync(http.Request, beneficiaryId, service, http.RequestAborted);
            await Results.Json(result, statusCode: StatusCodes.Status201Created).ExecuteAsync(http);
        }
        catch (Exception error) when (BeneficiaryInteractionWire.IsProblem(error))
        {
            await BeneficiaryInteractionWire.WriteProblemAsync(http, error);
        }
        finally
        {
            gate.Active = false;
        }
    }

    private sealed class ActivationOptions
    {
        public ActivationOptions() { }
    }

    private sealed class LocalLinkedWrite { }

    private sealed class LinkedWriteGate(IHttpContextAccessor http) : IActionWriteActivationGate
    {
        internal bool Active { get; set; }

        public void DemandActionWrite()
        {
            var context = http.HttpContext;
            if (!Active || context?.GetEndpoint()?.Metadata.GetMetadata<LocalLinkedWrite>() is null
                || context.User.Identity?.IsAuthenticated != true)
                throw new TenantAccessException(AccessFailure.Unavailable);
        }

        public void DemandActionChange(ActionChangeKind kind) =>
            throw new TenantAccessException(AccessFailure.Unavailable);
    }
}

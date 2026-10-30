using AuthManager.Application.Provisioning;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Provisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zeka.AuthOwnerProvision;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { return await ExecuteAsync(args, Console.OpenStandardInput(), Console.Out); }
        catch { return 6; } // A failed receipt channel must never print a native error.
    }

    internal static async Task<int> ExecuteAsync(
        string[] args, Stream input, TextWriter output, CancellationToken cancellationToken = default)
    {
        string stage = "admission";
        var businessCompleted = false;
        AssemblyBinding? binding = null;
        AdmittedOwnerInput? admitted = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            if (args.Length != 0) throw new OwnerToolException("ARGUMENTS_REJECTED");
            using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            readLimit.CancelAfter(TimeSpan.FromSeconds(15));
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var count = await input.ReadAsync(chunk, readLimit.Token);
                if (count == 0) break;
                if (buffer.Length + count > OwnerProtocol.MaximumInputBytes)
                    throw new OwnerToolException("INPUT_SIZE_REJECTED");
                buffer.Write(chunk, 0, count);
            }
            var request = OwnerProtocol.Parse(buffer.ToArray());
            binding = AssemblyBinding.Read();
            admitted = OwnerProtocol.Admit(request, binding);
            stage = "database-identity";
            await RuntimeAdmission.ValidateAsync(admitted, deadline.Token);

            stage = "provisioning";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [], EnvironmentName = "Production"
            });
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<AuthDbContext>(options =>
                options.UseNpgsql(request.AuthRuntimeConnectionString,
                    provider => provider.CommandTimeout(30)));
            builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
            builder.ConfigureMicrosoftIdentity();
            builder.Services.AddSingleton(admitted);
            builder.Services.AddScoped<IProvisioningIdentitySource, OwnerIdentitySource>();
            builder.Services.AddScoped<IProvisioningTransaction, OrganisationProvisioningTransaction>();
            builder.Services.AddScoped<IOrganisationProvisioningRepository, OrganisationProvisioningRepository>();
            builder.Services.AddSingleton(new OrganisationProvisioningOptions
            {
                ProcessingTimeout = TimeSpan.FromSeconds(request.ProcessingTimeoutSeconds),
                Retention = TimeSpan.FromSeconds(request.RetentionSeconds)
            });
            builder.Services.AddScoped<OrganisationProvisioningService>();
            // Build only the DI provider: no WebApplication Build/Run or HTTP listener.
            OrganisationProvisioningResult result;
            await using (var services = builder.Services.BuildServiceProvider())
            {
                await using var scope = services.CreateAsyncScope();
                result = await scope.ServiceProvider.GetRequiredService<OrganisationProvisioningService>()
                    .ProvisionAsync(new(request.DisplayName, request.Slug, request.IdempotencyKey, null), deadline.Token);
                businessCompleted = true;
            }
            var success = result.Outcome is OrganisationProvisioningOutcome.Created or OrganisationProvisioningOutcome.Replay;
            var receipt = new OwnerReceipt("zeka-m1-owner-provision-result/v1",
                success ? "PASS" : "BLOCKED",
                result.Outcome switch
                {
                    OrganisationProvisioningOutcome.Created => "OWNER_PROVISIONED",
                    OrganisationProvisioningOutcome.Replay => "OWNER_PROVISIONING_REPLAY",
                    OrganisationProvisioningOutcome.Conflict => "OWNER_PROVISIONING_CONFLICT",
                    _ => "OWNER_PROVISIONING_PROCESSING"
                }, stage, success ? 0 : 4, binding.SourceCommit, binding.BinarySha256,
                result.SubjectId, result.OrganisationId == Guid.Empty ? null : result.OrganisationId,
                result.MembershipId == Guid.Empty ? null : result.MembershipId);
            return await EmitAsync(output, receipt) ? receipt.ExitCode : 6;
        }
        catch (Exception error)
        {
            var (code, exit) = error switch
            {
                OwnerToolException safe => (safe.Code, 2),
                _ when businessCompleted => ("OWNER_PROVISIONING_REQUIRES_INSPECTION", 5),
                OperationCanceledException => ("OWNER_PROVISIONING_DEADLINE", 124),
                OrganisationProvisioningPersistenceException persistence when persistence.RequiresInspection =>
                    ("OWNER_PROVISIONING_REQUIRES_INSPECTION", 5),
                OrganisationProvisioningException => ("OWNER_PROVISIONING_REJECTED", 4),
                _ => (stage == "database-identity" ? "RUNTIME_IDENTITY_REJECTED" : "OWNER_PROVISIONING_FAILED", 3)
            };
            // Never serialize a request, exception, provider diagnostic, connection,
            // password, environment, argument, or inner exception.
            var receipt = new OwnerReceipt("zeka-m1-owner-provision-result/v1", "BLOCKED",
                code, stage, exit, admitted is null ? null : binding?.SourceCommit,
                admitted is null ? null : binding?.BinarySha256);
            return await EmitAsync(output, receipt) ? exit : 6;
        }
    }
    private static async Task<bool> EmitAsync(TextWriter output, OwnerReceipt receipt)
    {
        try
        {
            await output.WriteLineAsync(OwnerReceipt.Serialize(receipt));
            await output.FlushAsync();
            return true;
        }
        catch { return false; }
    }
}

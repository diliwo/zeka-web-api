using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

public sealed class BearerValidationTests : Zeka.Authentication.Tests.ValidatorContractTests
{
    protected override void Register(IServiceCollection services, IConfiguration configuration)
        => AdminAreaManagement.API.TenantAuthentication.AddTenantAuthentication(services, configuration);
}

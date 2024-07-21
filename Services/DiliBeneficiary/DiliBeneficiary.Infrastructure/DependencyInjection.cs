using DiliBeneficiary.Application.Common.Interfaces;
using DiliBeneficiary.Core.Interfaces;
using DiliBeneficiary.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DiliBeneficiary.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTransient<IFileService, FileService>(); ;
        return services;
    }
}
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace AuthManagement.Infrastructure.Persistence;

public static class AuthContextDatabaseMigration
{
    public static void MigrateDatabase(this WebApplication webApp)
    {
        using var scope = webApp.Services.CreateScope();
        using var authContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        authContext.Database.Migrate();
    }
}
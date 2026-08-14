using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthManager.Tests;

internal sealed class IdentityTestContext : IAsyncDisposable
{
    private IdentityTestContext(SqliteConnection connection, ServiceProvider services)
    {
        Connection = connection;
        Services = services;
    }

    public SqliteConnection Connection { get; }

    public ServiceProvider Services { get; }

    public static async Task<IdentityTestContext> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite(connection));
        builder.ConfigureMicrosoftIdentity();

        var services = builder.Services.BuildServiceProvider();

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new IdentityTestContext(connection, services);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

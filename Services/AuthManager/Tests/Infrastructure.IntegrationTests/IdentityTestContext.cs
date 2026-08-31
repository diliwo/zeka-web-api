using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Infrastructure.IntegrationTests;

internal sealed class IdentityTestContext : IAsyncDisposable
{
    private IdentityTestContext(SqliteConnection connection, ServiceProvider services)
    {
        Connection = connection;
        Services = services;
    }

    public SqliteConnection Connection { get; }

    public ServiceProvider Services { get; }

    public static async Task<IdentityTestContext> CreateAsync(
        DateTimeOffset? utcNow = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var databaseName = $"auth-tests-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
        builder.ConfigureMicrosoftIdentity();

        if (utcNow is not null)
        {
            var timeProvider = new Mock<TimeProvider>();
            timeProvider.Setup(provider => provider.GetUtcNow()).Returns(utcNow.Value);
            builder.Services.RemoveAll<TimeProvider>();
            builder.Services.AddSingleton(timeProvider.Object);
        }

        configureServices?.Invoke(builder.Services);

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

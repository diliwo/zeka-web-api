using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Authentication;
using AuthManager.Application.Workers;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Authentication;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Workers;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class WorkerRotationPostgreSqlTests(WorkerRotationPostgreSqlFixture fixture, ITestOutputHelper output)
    : IClassFixture<WorkerRotationPostgreSqlFixture>
{
    [Theory]
    [InlineData("prepare", "revoked-session")]
    [InlineData("revoke-others", "revoked-session")]
    [InlineData("prepare", "changed-stamp")]
    [InlineData("prepare", "suspended-owner")]
    public async Task OwnerAuthorityChangedAfterPrecheckCannotRotateWorkers(string phase, string change)
    {
        var connection = await fixture.CreateDatabaseAsync();
        output.WriteLine("PostgreSQL {0}; fixture container {1}", fixture.ServerVersion, fixture.ContainerId);
        var clock = new FrozenClock();
        using var issuer = new CountingIssuer(clock);
        await issuer.InitializeAsync();
        var subject = Guid.NewGuid();
        var organisation = Guid.NewGuid();
        var registry = Guid.NewGuid();
        var caller = new WorkerRotationCaller(subject, Guid.NewGuid());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "LocalStack" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["M1WorkerRotation:Enabled"] = "true",
            ["M1WorkerRotation:OwnerSubjectId"] = subject.ToString(),
            ["M1WorkerRotation:OrganisationId"] = organisation.ToString(),
            ["M1WorkerRotation:RegistryId"] = registry.ToString(),
            ["M1WorkerRotation:ProcessingTimeoutSeconds"] = "120",
            ["M1WorkerRotation:RetentionSeconds"] = "86400"
        });
        builder.Services.AddDbContext<AuthDbContext>(options =>
            options.UseNpgsql(connection, provider => provider.CommandTimeout(10)));
        builder.ConfigureMicrosoftIdentity();
        builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<ISessionAccessTokenIssuer>(issuer);
        builder.Services.AddSingleton(new BrowserSessionPolicy());
        builder.Services.AddScoped<IBrowserSessions, BrowserSessions>();
        builder.Services.AddM1WorkerRotation(builder.Configuration, builder.Environment);
        await using var services = builder.Services.BuildServiceProvider();

        await using (var setup = services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AuthDbContext>();
            var owner = User.Create("operator@example.invalid", "operator", "Synthetic", "Owner", clock.GetUtcNow());
            owner.Id = subject;
            owner.EmailConfirmed = true;
            owner.ActivateConfirmedIdentity(clock.GetUtcNow());
            Assert.True((await setup.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(owner)).Succeeded);
            var org = Organisation.Create(organisation, "Synthetic rotation", subject, clock.GetUtcNow());
            Assert.True(org.Activate(clock.GetUtcNow()));
            db.Organisations.Add(org);
            db.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation, subject, clock.GetUtcNow()));
            db.BrowserSessionFamilies.Add(new BrowserSessionFamily
            {
                Id = caller.SessionId, UserId = subject, SecurityStamp = owner.SecurityStamp!,
                CreatedAtUtc = clock.GetUtcNow(), IdleExpiresAtUtc = clock.GetUtcNow().AddHours(1),
                AbsoluteExpiresAtUtc = clock.GetUtcNow().AddHours(1)
            });
            await db.SaveChangesAsync();
        }

        var request = new WorkerRotationRequest(Guid.NewGuid(), organisation);
        WorkerDeliveryReceipt[]? deliveries = null;
        if (phase == "revoke-others")
        {
            await using var positive = services.CreateAsyncScope();
            var rotation = positive.ServiceProvider.GetRequiredService<WorkerRotationService>();
            var earlier = await rotation.ExecuteAsync("prepare", caller, new(Guid.NewGuid(), organisation));
            Assert.Equal("ISSUED", earlier.State);
            var current = await rotation.ExecuteAsync("prepare", caller, request);
            Assert.Equal("ISSUED", current.State);
            Assert.Equal(4, issuer.Issues);
            deliveries = current.Workers.Select(worker => new WorkerDeliveryReceipt(worker.Profile,
                current.OperationId, worker.Generation, worker.SubjectId, worker.SessionId, worker.PayloadSha256!,
                worker.PublicationSha256!, WorkerRotationService.ExpectedVolume(worker.Profile, registry),
                "/run/zeka-worker-credential/current.json", 1654, 1654, "0500", "0400",
                worker.Profile == WorkerProfiles.AdminAreaRetry ? "adminarea" : "client", true)).ToArray();
        }

        await using var scopeA = services.CreateAsyncScope();
        var contextA = scopeA.ServiceProvider.GetRequiredService<AuthDbContext>();
        var sessions = scopeA.ServiceProvider.GetRequiredService<IBrowserSessions>();
        Assert.Equal(BrowserSessionOutcome.Success, await sessions.CheckActiveAsync(subject, caller.SessionId, default));
        Assert.Null(contextA.Database.CurrentTransaction);
        var trackedFamily = Assert.Single(contextA.ChangeTracker.Entries<BrowserSessionFamily>()).Entity;
        var trackedOwner = Assert.Single(contextA.ChangeTracker.Entries<User>()).Entity;
        var originalStamp = trackedOwner.SecurityStamp;

        await using (var scopeB = services.CreateAsyncScope())
        {
            var contextB = scopeB.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.NotSame(contextA, contextB);
            await using var transaction = await contextB.Database.BeginTransactionAsync();
            if (change == "revoked-session")
                (await contextB.BrowserSessionFamilies.SingleAsync(x => x.Id == caller.SessionId)).RevokedAtUtc = clock.GetUtcNow();
            else
            {
                var owner = await contextB.Users.SingleAsync(x => x.Id == subject);
                if (change == "changed-stamp")
                    owner.SecurityStamp = Guid.NewGuid().ToString("N");
                else
                    contextB.Entry(owner).Property(x => x.Status).CurrentValue = UserStatus.Suspended;
            }
            await contextB.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.Null(trackedFamily.RevokedAtUtc);
        Assert.Equal(UserStatus.Active, trackedOwner.Status);
        Assert.Equal(originalStamp, trackedOwner.SecurityStamp);
        await using var observer = services.CreateAsyncScope();
        var database = observer.ServiceProvider.GetRequiredService<AuthDbContext>();
        var persistedFamily = await database.BrowserSessionFamilies.AsNoTracking().SingleAsync(x => x.Id == caller.SessionId);
        var persistedOwner = await database.Users.AsNoTracking().SingleAsync(x => x.Id == subject);
        if (change == "revoked-session")
            Assert.Equal(clock.GetUtcNow(), persistedFamily.RevokedAtUtc);
        else if (change == "changed-stamp")
            Assert.NotEqual(originalStamp, persistedOwner.SecurityStamp);
        else
            Assert.Equal(UserStatus.Suspended, persistedOwner.Status);

        var before = await ReadEffectsAsync(database, subject, issuer.Issues);
        var service = scopeA.ServiceProvider.GetRequiredService<WorkerRotationService>();
        var exception = await Record.ExceptionAsync(() => service.ExecuteAsync(phase, caller, request, deliveries));
        var after = await ReadEffectsAsync(database, subject, issuer.Issues);
        output.WriteLine("Precheck committed; independent {0} committed; stale request resumed. Rejection {1}. Effects unchanged {2}.",
            change, (exception as WorkerRotationException)?.Code ?? (exception is null ? "NONE" : exception.GetType().Name), before == after);

        var rejection = Assert.IsType<WorkerRotationException>(exception);
        Assert.Equal("WORKER_ROTATION_CURRENT_OWNER_REJECTED", rejection.Code);
        Assert.False(rejection.RequiresInspection);
        Assert.Equal(before, after);
    }

    private static async Task<Effects> ReadEffectsAsync(AuthDbContext database, Guid owner, int issued)
    {
        var workers = await database.Users.Where(x => x.Id != owner).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.EmailConfirmed, x.SecurityStamp }).ToArrayAsync();
        var memberships = await database.OrganisationMemberships.Where(x => x.UserId != owner).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.UserId, x.OrganisationId, x.PermissionSetId, x.Status }).ToArrayAsync();
        var families = await database.BrowserSessionFamilies.Where(x => x.UserId != owner).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.UserId, x.RevokedAtUtc, x.IdleExpiresAtUtc, x.AbsoluteExpiresAtUtc }).ToArrayAsync();
        var stateHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { workers, memberships, families })));
        return new(issued, stateHash, await database.IdempotencyRecords.CountAsync(), await database.AuditEntries.CountAsync());
    }

    private sealed record Effects(int Issued, string WorkerStateHash, int LedgerRows, int AuditRows);

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class CountingIssuer(TimeProvider clock) : ISessionAccessTokenIssuer, IDisposable
    {
        private readonly Zeka.Authentication.Tests.AuthenticationFixture fixture = new();
        private AccessTokenIssuer issuer = null!;
        public int Issues { get; private set; }

        public async Task InitializeAsync()
        {
            issuer = new AccessTokenIssuer(new IssuerContractTests.TestKeys(fixture.Key), new IssuerOptions
            {
                ActiveKeyId = Zeka.Authentication.Tests.AuthenticationFixture.KeyId,
                ActiveSigningKeyReference = Path.Combine(Path.GetTempPath(), "not-provisioned-rotation-test"),
                AccessTokenLifetime = TimeSpan.FromMinutes(30)
            }, Zeka.Authentication.AuthenticationOptions.Read(fixture.Configuration()), clock);
            issuer.ConfirmPublication(await issuer.PrepareAsync());
        }

        public Task<string> IssueSessionAsync(Guid subject, Guid session, TimeSpan lifetime, CancellationToken ct)
        {
            Issues++;
            return issuer.IssueSessionAsync(subject, session, lifetime, ct);
        }

        public void Dispose() => fixture.Dispose();
    }
}

public sealed class WorkerRotationPostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
        .WithLabel(ResourceReaper.ResourceReaperSessionLabel, Guid.NewGuid().ToString("D"))
        .WithLabel("zeka.auth.rotation-fixture", "owner-authority-regression")
        .WithCreateParameterModifier(parameters =>
            parameters.HostConfig.PortBindings["5432/tcp"].Single().HostIP = "127.0.0.1")
        .Build();

    public string ContainerId => container.Id;
    public string ServerVersion { get; private set; } = "";

    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await container.StartAsync(deadline.Token);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync(deadline.Token);
        Assert.Equal(17, connection.PostgreSqlVersion.Major);
        ServerVersion = connection.ServerVersion;
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"auth_rotation_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
        await using (var sql = new NpgsqlConnection(connection))
        {
            await sql.OpenAsync();
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            const string bootstrap = "Deployments/database/bootstrap-auth-roles.sql";
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, bootstrap)))
                directory = directory.Parent;
            Assert.NotNull(directory);
            await using var command = sql.CreateCommand();
            command.CommandText = await File.ReadAllTextAsync(Path.Combine(directory!.FullName, bootstrap));
            await command.ExecuteNonQueryAsync();
        }
        await using var migrator = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
        await migrator.Database.OpenConnectionAsync();
        await migrator.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
        await migrator.Database.MigrateAsync();
        return connection;
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

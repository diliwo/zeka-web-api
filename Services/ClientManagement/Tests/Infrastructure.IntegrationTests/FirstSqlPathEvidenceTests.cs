using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ClientManagement.API.Services;
using ClientManagement.Application.Clients.Commands.AddClient;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Enums;
using ClientManagement.Core.Interfaces;
using ClientManagement.Core.ValueObjects;
using ClientManagement.Infrastructure.Messaging;
using ClientManagement.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Extensions.MultiTenancy.AspNetCore;
using Zeka.PersistenceSecurity;
using Zeka.PersistenceSecurity.Tests;

namespace Infrastructure.IntegrationTests;

public sealed class ClientFirstSqlPathEvidenceTests(PostgreSqlClientRuntimeDatabase database)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    [Fact]
    public async Task Client_http_get_and_post_prove_first_sql_order_on_restricted_runtime()
    {
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(organisation, evidence);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, organisation);

        var get = await client.GetAsync("/api/Clients");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfRead, 1);

        evidence.Clear();
        var post = await client.PostAsJsonAsync("/api/Clients", new AddClientCommand
        {
            ReferenceNumber = "path-reference",
            CivilStatus = CivilStatus.Single,
            FirstName = "Synthetic",
            LastName = "Evidence",
            Gender = Gender.Diverse,
            BirthDate = new DateTime(1980, 3, 15),
            PlaceOfBirth = "Test place",
            Nationality = "Test nationality",
            Ssn = ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: 91),
            Email = "path@example.invalid",
            Phone = "0123456789",
            MobilePhone = "0123456789",
            Address = new Address("1", "Test street", "1000", "Test city", "Test country")
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfWrite, 1);
    }

    [Fact]
    public async Task Client_consumer_proves_first_sql_order_on_restricted_runtime()
    {
        var organisation = Guid.NewGuid();
        var membership = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        var configuration = RuntimeConfiguration();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        services.AddSingleton<IInterceptor>(evidence);
        services.RemoveAll<IHostedService>();
        services.RemoveAll<ITenantAttemptOrderObserver>();
        services.AddSingleton<ITenantAttemptOrderObserver>(evidence);
        services.AddSingleton<IHttpClientFactory>(new ConsumerHttpClientFactory());
        await using var provider = services.BuildServiceProvider();

        var consumer = new StaffProjectionConsumer(provider.GetRequiredService<IServiceScopeFactory>(), configuration);
        await consumer.Handle(new StaffProjectionChangedV1(organisation, membership, 1, true,
            "Synthetic", "Consumer", "consumer", "Team", "T"));

        var attempts = AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfRead, 2);
        Assert.Contains(attempts[1], item => item.Category == TenantAttemptCommandCategory.EfWrite);
    }

    private async Task<WebApplication> StartHttpApplicationAsync(Guid organisation,
        TenantAttemptEvidenceCollector evidence)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        var configuration = RuntimeConfiguration();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddTenantContext();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<TenantRequestIdentity>();
        builder.Services.AddScoped<IOperationIdentity>(sp => sp.GetRequiredService<TenantRequestIdentity>());
        builder.Services.AddScoped<ClientManagement.Infrastructure.Authorization.ITenantAccessCredential>(
            sp => sp.GetRequiredService<TenantRequestIdentity>());
        builder.Services.AddSingleton<ICurrentUserService, CurrentUserService>();
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        ClientManagement.Application.DependencyInjection.AddApplication(builder.Services);
        ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<IInterceptor>(evidence);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<ITenantAttemptOrderObserver>();
        builder.Services.AddSingleton<ITenantAttemptOrderObserver>(evidence);
        builder.Services.RemoveAll<ICurrentTenantAccess>();
        builder.Services.AddScoped<ICurrentTenantAccess>(_ => new Access(organisation));
        builder.Services.AddControllers().AddApplicationPart(typeof(ClientManagement.API.Controllers.ClientsController).Assembly);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<TenantAccessMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private ConfigurationManager RuntimeConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/",
            ["TenantWorker:SubjectId"] = "worker",
            ["TenantWorker:BearerToken"] = "synthetic-test-token",
            ["FileServerPath"] = Path.GetTempPath()
        });
        return configuration;
    }

    private static TenantAttemptOrderEvent[][] AssertCompleteAttempts(TenantAttemptEvidenceCollector evidence,
        TenantAttemptCommandCategory required, int expectedAttempts) =>
        TenantAttemptEvidenceAssertions.CompleteAttempts(evidence.Snapshot(), evidence.BackendPids(),
            required, expectedAttempts);

    private static void SetTenantHeaders(HttpClient client, Guid organisation)
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-test-token");
        client.DefaultRequestHeaders.Add("X-Organisation-Id", organisation.ToString());
    }

    private sealed class Access(Guid organisation) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId,
            CancellationToken cancellationToken) => Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Authorized,
            new(organisation, Guid.NewGuid(), ["Clients.ViewAll", "Clients.Create"], "test", DateTimeOffset.UtcNow)));
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Issue45Test";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "subject"),
                new Claim(ClaimTypes.Name, "synthetic")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class ConsumerHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ConsumerAuthorityHandler(), disposeHandler: true);
    }

    private sealed class ConsumerAuthorityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/memberships/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    ContractVersion = 1,
                    SubjectId = "worker",
                    OrganisationId = Guid.Parse(request.RequestUri.Segments.Last()),
                    OrganisationMembershipId = Guid.NewGuid(),
                    EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" },
                    DecisionVersion = "test",
                    ObservedAtUtc = DateTimeOffset.UtcNow
                })
            });
        }
    }
}

public sealed class PostgreSqlClientRuntimeDatabase : IAsyncLifetime
{
    private const string MigratorPassword = "test-migrator-password";
    private const string RuntimePassword = "test-runtime-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public string RuntimeConnectionString => Connection("zeka_client_runtime", RuntimePassword);

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-client-roles.sql"));
        await ExecuteAdministratorAsync(
            $"ALTER ROLE zeka_client_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_client_runtime PASSWORD '{RuntimePassword}';");
        await using var deployment = Deployment(Connection("zeka_client_migrator", MigratorPassword));
        await deployment.Database.OpenConnectionAsync();
        await deployment.Database.ExecuteSqlRawAsync("SET ROLE zeka_client_owner");
        await deployment.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"__OrganisationTenantMap\" (\"TenantName\" text PRIMARY KEY, \"OrganisationId\" uuid NOT NULL UNIQUE)");
        await deployment.GetService<IMigrator>().MigrateAsync();
        await RlsSecurityManifestVerifier.VerifyAsync(deployment, typeof(ApplicationDbContext).Assembly);
        await new RuntimeDatabaseIdentityValidator(RuntimeConnectionString, "zeka_client_runtime").StartAsync(default);
    }

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    private static DeploymentDbContext Deployment(string connectionString) => new(
        new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(connectionString).Options);

    private string Connection(string username, string password) => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
    { Username = username, Password = password }.ConnectionString;

    private async Task ExecuteAdministratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadBootstrapScript(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed database role bootstrap script was not found.", fileName);
    }
}

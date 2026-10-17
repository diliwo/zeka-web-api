using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClientManagement.API.Controllers;
using ClientManagement.API.Services;
using ClientManagement.Application;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using ClientManagement.Infrastructure;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Tests.Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using Zeka.Extensions.MultiTenancy.AspNetCore;

namespace Infrastructure.IntegrationTests;

// Real controller, request identity, MediatR policy, dossier predicate and PostgreSQL runtime/RLS.
// Only authentication and the remote current-access decision use synthetic test adapters.
public sealed class DossierLookupHttpTests(PostgreSqlClientRuntimeDatabase database)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    private static int sequence = 9100;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorized_dossier_preserves_the_existing_success_schema(bool assignedOnly)
    {
        var access = new Access(assignedOnly);
        var id = await Seed(access.Organisation, access.Membership);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        using var response = await client.GetAsync($"/api/clients/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, json.RootElement.GetProperty("clientId").GetInt32());
        Assert.Equal("Synthetic", json.RootElement.GetProperty("firstName").GetString());
        Assert.Equal("Test street", json.RootElement.GetProperty("address").GetProperty("street").GetString());
        // Snapshot of the existing successful wire shape, including unchanged legacy fields.
        var expected = new[] { "clientId", "referenceNumber", "civilStatus", "firstName", "lastName",
            "gender", "birthDate", "placeOfBirth", "nationality", "niss", "email", "phone", "mobilePhone",
            "supportStaffMemberName", "supportStaffMemberService", "supportStartDate", "supportEndDate",
            "nativeLanguage", "contactLanguage", "address", "age", "socialWorkerName", "supports",
            "lastTrainingName", "lastTrainingNote", "lastTrainingResult", "lastTrainingStartDate",
            "lastTrainingEndDate", "lastJobExperienceCompanyName", "lastJobExperienceContractTypeName",
            "lastJobExperienceFunction", "lastJobExperienceStartDate", "lastJobExperienceEndDate" };
        Assert.Equal(expected.OrderBy(x => x), json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_and_cross_tenant_dossiers_have_the_same_non_disclosing_404(bool assignedOnly)
    {
        var access = new Access(assignedOnly);
        var foreignId = await Seed(Guid.NewGuid(), Guid.NewGuid());
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        Assert.Equal(await NotFound(client, int.MaxValue), await NotFound(client, foreignId));
    }

    [Fact]
    public async Task Unassigned_same_tenant_dossier_is_indistinguishable_from_missing()
    {
        var access = new Access(assignedOnly: true);
        var hiddenId = await Seed(access.Organisation, Guid.NewGuid());
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        Assert.Equal(await NotFound(client, int.MaxValue), await NotFound(client, hiddenId));
    }

    [Fact]
    public async Task Revoked_permission_remains_403_for_existing_and_missing_dossiers()
    {
        var access = new Access(assignedOnly: true);
        var id = await Seed(access.Organisation, access.Membership);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        using var before = await client.GetAsync($"/api/clients/{id}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        access.Permissions = [];
        foreach (var target in new[] { id, int.MaxValue })
        {
            using var denied = await client.GetAsync($"/api/clients/{target}");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(await denied.Content.ReadAsStringAsync());
        }
        Assert.Equal(3, access.Calls); // No cached positive decision across requests.
    }

    [Theory]
    [InlineData(TenantAccessOutcome.Unauthenticated, HttpStatusCode.Unauthorized)]
    [InlineData(TenantAccessOutcome.Denied, HttpStatusCode.Forbidden)]
    [InlineData(TenantAccessOutcome.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Authority_failures_are_never_disguised_as_missing(TenantAccessOutcome outcome, HttpStatusCode status)
    {
        var access = new Access(assignedOnly: false);
        var id = await Seed(access.Organisation, access.Membership);
        access.Outcome = outcome;
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        foreach (var target in new[] { id, int.MaxValue })
        {
            using var response = await client.GetAsync($"/api/clients/{target}");
            Assert.Equal(status, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Unauthenticated_http_request_is_challenged_before_current_access_or_resource_lookup()
    {
        var access = new Access(assignedOnly: false);
        await using var app = await Host(access);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/clients/{int.MaxValue}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, access.Calls);
    }

    private static async Task<string> NotFound(HttpClient client, int id)
    {
        using var response = await client.GetAsync($"/api/clients/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", root.GetProperty("title").GetString());
        // MVC's standard client error response may carry an opaque request trace ID.
        // All resource-independent fields must match; no resource ID, instance or detail is allowed.
        Assert.All(root.EnumerateObject(), field =>
            Assert.Contains(field.Name, new[] { "type", "title", "status", "traceId" }));
        return JsonSerializer.Serialize(root.EnumerateObject().Where(x => x.Name != "traceId")
            .OrderBy(x => x.Name).ToDictionary(x => x.Name, x => x.Value.Clone()));
    }

    private async Task<WebApplication> Host(Access access)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["FileServerPath"] = Path.GetTempPath()
        });
        builder.Services.AddTenantContext();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ClientManagement.Core.Interfaces.ICurrentUserService, CurrentUserService>();
        builder.Services.AddScoped<IOperationIdentity, TenantRequestIdentity>();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<ICurrentTenantAccess>();
        builder.Services.AddSingleton<ICurrentTenantAccess>(access);
        builder.Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(ClientsController).Assembly).AddNewtonsoftJson();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<TenantAccessMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, Guid organisation)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "synthetic-worker");
        client.DefaultRequestHeaders.Add("X-Organisation-Id", organisation.ToString("D"));
        return client;
    }

    private async Task<int> Seed(Guid organisation, Guid membership)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "SELECT set_config('zeka.organisation_id', @organisation, true)", connection, tx))
        {
            command.Parameters.AddWithValue("organisation", organisation.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(organisation));
        await context.Database.UseTransactionAsync(tx);
        var client = SyntheticClient.Create(SyntheticClient.Niss(sequence: Interlocked.Increment(ref sequence)));
        client.ReferenceNumber = $"dossier-test-{Guid.NewGuid():N}";
        var worker = new SocialWorker();
        worker.ApplyProjection(membership, 1, Guid.NewGuid(), true, "Synthetic", "Worker",
            Guid.NewGuid().ToString("N"), "Team", "T");
        context.Add(new SocialCase(client, DateTime.Today.AddDays(-2), worker));
        await context.SaveChangesAsync();
        await tx.CommitAsync();
        return client.Id;
    }

    private sealed class Access(bool assignedOnly) : ICurrentTenantAccess
    {
        public Guid Organisation { get; } = Guid.NewGuid();
        public Guid Membership { get; } = Guid.NewGuid();
        public string[] Permissions { get; set; } = [assignedOnly ? "Clients.ViewAssigned" : "Clients.ViewAll"];
        public TenantAccessOutcome Outcome { get; set; } = TenantAccessOutcome.Authorized;
        public int Calls { get; private set; }
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid selected, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new TenantAccessDecision(Outcome, Outcome == TenantAccessOutcome.Authorized
                ? new(Organisation, Membership, Permissions, "test", DateTimeOffset.UtcNow) : null));
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "DossierLookupTest";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.ContainsKey("X-Test-User")
                ? AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "synthetic-worker")], SchemeName)), SchemeName))
                : AuthenticateResult.NoResult());
    }
}
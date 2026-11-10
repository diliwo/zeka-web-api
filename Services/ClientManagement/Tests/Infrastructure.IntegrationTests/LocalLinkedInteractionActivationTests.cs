using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ClientManagement.API;
using ClientManagement.API.LocalDevelopment;
using ClientManagement.API.Services;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.BeneficiaryB1;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Zeka.Authentication;
using Zeka.Authentication.Tests;
using Zeka.Extensions.MultiTenancy.AspNetCore;

namespace Infrastructure.IntegrationTests;

public sealed class LocalLinkedInteractionActivationTests
{
    private const string InteractionPath = "/api/v1/beneficiaries/42/interactions";

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task LocalStack_without_opt_in_has_no_route_and_never_resolves_authority(string? flag)
    {
        using var jwt = new AuthenticationFixture();
        await using var app = Build("LocalStack", flag, jwt, (_, _, _) =>
            throw new InvalidOperationException("An inactive adapter must not ask for authority."));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = Request(jwt.Token(), Guid.NewGuid());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scope = app.Services.CreateScope();
        Assert.IsType<ClosedActionWriteActivationGate>(
            scope.ServiceProvider.GetRequiredService<IActionWriteActivationGate>());
    }

    [Theory]
    [InlineData("LocalStack")]
    [InlineData("localstack")]
    [InlineData("Development")]
    [InlineData("development")]
    public async Task Opt_in_uses_real_bearer_validation_and_current_subject_in_eligible_hosts(string environment)
    {
        using var jwt = new AuthenticationFixture();
        var subject = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var observed = new List<(string Subject, Guid Tenant)>();
        await using var app = Build(environment, "true", jwt, (caller, selected, services) =>
        {
            var gate = services.GetRequiredService<IActionWriteActivationGate>();
            gate.DemandActionWrite();
            foreach (var kind in Enum.GetValues<ActionChangeKind>())
                AssertUnavailable(() => gate.DemandActionChange(kind));
            observed.Add((caller, selected));
        });
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var anonymous = Request(null, tenant);
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        using var invalid = Request(jwt.Token("signature"), tenant);
        using var invalidResponse = await client.SendAsync(invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
        Assert.Empty(observed);

        using var genuine = Request(jwt.Token(subject: subject), tenant);
        genuine.Headers.Add("X-Test-Subject", Guid.NewGuid().ToString());
        genuine.Headers.Add("X-Role", "Owner");
        using var denied = await client.SendAsync(genuine);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        Assert.Equal((subject, tenant), Assert.Single(observed));

        using var scope = app.Services.CreateScope();
        var background = scope.ServiceProvider.GetRequiredService<IActionWriteActivationGate>();
        AssertUnavailable(background.DemandActionWrite);
        foreach (var kind in Enum.GetValues<ActionChangeKind>())
            AssertUnavailable(() => background.DemandActionChange(kind));
        AssertUnavailable(() => _ = scope.ServiceProvider
            .GetRequiredService<IActionAuthorityServiceCredential>().BearerToken);
    }

    [Theory]
    [InlineData("Test", "true")]
    [InlineData("Production", "true")]
    [InlineData("production", "true")]
    [InlineData("Staging", "true")]
    [InlineData("Custom", "true")]
    [InlineData("LocalStack", "malformed")]
    [InlineData("Development", "")]
    public async Task Ineligible_opt_in_or_invalid_configuration_rejects_host_start(string environment, string flag)
    {
        using var jwt = new AuthenticationFixture();
        await using var app = Build(environment, flag, jwt);
        await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
    }

    [Theory]
    [InlineData("https://authority.example.invalid", true)]
    [InlineData("http://authority.example.invalid", false)]
    [InlineData("https://authority.example.invalid/unapproved-path", false)]
    public async Task Protected_file_reader_is_endpoint_scoped_bounded_and_rereads_on_access(string origin, bool allowed)
    {
        using var jwt = new AuthenticationFixture();
        var file = Path.GetTempFileName();
        try
        {
            var first = jwt.Token();
            var second = jwt.Token();
            await using var app = Build("LocalStack", "true", jwt, (_, _, services) =>
            {
                var credential = services.GetRequiredService<IActionAuthorityServiceCredential>();
                if (!allowed)
                {
                    AssertUnavailable(() => _ = credential.BearerToken);
                    return;
                }

                AssertUnavailable(() => _ = credential.BearerToken);
                File.WriteAllText(file, first + "\n");
                Assert.Equal(first, credential.BearerToken);
                File.WriteAllText(file, second + "\r\n");
                Assert.Equal(second, credential.BearerToken);
                File.WriteAllText(file, second + "\n\n");
                AssertUnavailable(() => _ = credential.BearerToken);
                File.WriteAllText(file, new string('a', 8193));
                AssertUnavailable(() => _ = credential.BearerToken);
            }, origin, file);
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var request = Request(jwt.Token(), Guid.NewGuid());
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_string_value_or_property_name_returns_a_redacted_problem(bool escapedName)
    {
        using var jwt = new AuthenticationFixture();
        await using var app = Build("LocalStack", "true", jwt, (_, _, _) =>
            throw new InvalidOperationException("Invalid input must not ask for authority."));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = Request(jwt.Token(), Guid.NewGuid(), "~");
        var content = request.Content!;
        if (escapedName)
        {
            var json = await content.ReadAsStringAsync();
            var malformed = json.Insert(1, "\"a\\uD800\":0,");
            request.Content = new StringContent(malformed, Encoding.UTF8, "application/json");
        }
        else
        {
            var body = await content.ReadAsByteArrayAsync();
            var index = Array.IndexOf(body, (byte)'~');
            Assert.True(index >= 0);
            body[index] = 0xff;
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        content.Dispose();

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("""{"type":"about:blank","title":"Invalid request","status":400}""",
            await response.Content.ReadAsStringAsync());
    }

    private static WebApplication Build(string environment, string? flag, AuthenticationFixture jwt,
        Action<string, Guid, IServiceProvider>? observe = null, string? origin = null, string? file = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, Args = [] });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(jwt.Configuration().AsEnumerable());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalDevelopment:LinkedInteractionEnabled"] = flag,
            ["ActionAuthority:AuthManagementUrl"] = origin,
            ["LocalDevelopment:ActionAuthorityTokenFile"] = file
        });
        builder.Services.AddTenantAuthentication(builder.Configuration);
        builder.Services.AddSingleton<IJwksDocumentSource>(new DocumentSource(jwt.Jwks()));
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddTenantContext();
        builder.Services.AddScoped<IOperationIdentity, TenantRequestIdentity>();
        builder.Services.AddScoped<TenantOperation>();
        builder.Services.AddScoped<ICurrentTenantAccess>(sp => new DenyingAuthority((subject, tenant) =>
            observe?.Invoke(subject, tenant, sp)));
        builder.Services.AddSingleton<IActionWriteActivationGate, ClosedActionWriteActivationGate>();
        // The authority always denies, before this focused host can reach a store or transaction.
        builder.Services.AddScoped(sp => new BeneficiaryB1Service(null!, sp.GetRequiredService<TenantOperation>(),
            null!, TimeProvider.System));

        var enabled = builder.AddLocalLinkedInteraction();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        if (enabled) app.MapLocalLinkedInteraction();
        return app;
    }

    private static HttpRequestMessage Request(string? bearer, Guid tenant, string note = "Synthetic note")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, InteractionPath);
        request.Headers.Add("X-Organisation-Id", tenant.ToString());
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Content = JsonContent.Create(new
        {
            occurredAtUtc = "2026-10-01T00:00:00Z",
            conciseNote = note,
            idempotencyKey = Guid.NewGuid().ToString("D")
        });
        return request;
    }

    private static void AssertUnavailable(Action demand) =>
        Assert.Equal(AccessFailure.Unavailable, Assert.Throws<TenantAccessException>(demand).Failure);

    private sealed class DenyingAuthority(Action<string, Guid> observe) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid organisationId, CancellationToken cancellationToken)
        {
            observe(subject, organisationId);
            return Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Denied));
        }
    }
}

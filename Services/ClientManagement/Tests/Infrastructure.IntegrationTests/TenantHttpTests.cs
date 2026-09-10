using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using ClientManagement.API.Services;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class TenantHttpTests
{
    [Theory]
    [InlineData("issuer")] [InlineData("audience")] [InlineData("signature")] [InlineData("expiry")]
    public void Authentication_requires_a_valid_signature_issuer_audience_and_lifetime(string invalid)
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Issuer"] = "https://issuer.invalid", ["Authentication:Audience"] = "zeka-services",
                ["Authentication:SigningKey"] = key
            }).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        ClientManagement.API.TenantAuthentication.AddTenantAuthentication(services, configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        string Token(bool corrupt)
        {
            var signing = corrupt && invalid == "signature" ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)) : key;
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: corrupt && invalid == "issuer" ? "other" : "https://issuer.invalid",
                audience: corrupt && invalid == "audience" ? "other" : "zeka-services",
                claims: new[] { new Claim("sub", "subject") }, notBefore: DateTime.UtcNow.AddHours(-2),
                expires: corrupt && invalid == "expiry" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signing)),
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
            return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        }
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        Assert.True(handler.ValidateToken(Token(false), options.TokenValidationParameters, out _).Identity!.IsAuthenticated);
        Assert.ThrowsAny<Microsoft.IdentityModel.Tokens.SecurityTokenException>(() => handler.ValidateToken(Token(true), options.TokenValidationParameters, out _));
    }

    [Fact]
    public void Application_and_domain_do_not_reference_transport_or_postgresql_assemblies()
    {
        foreach (var assembly in new[] { typeof(TenantOperation).Assembly, typeof(ClientManagement.Core.Common.Entity).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || reference.Name.StartsWith("Npgsql", StringComparison.Ordinal)
                || reference.Name.StartsWith("Grpc", StringComparison.Ordinal));
    }

    [Fact]
    public void Http_metadata_derives_the_same_application_policy_for_request_parameters()
    {
        var controllers = typeof(TenantRequestIdentity).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t));
        var policies = 0;
        foreach (var action in controllers.SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)))
        {
            var metadata = action.GetCustomAttribute<TenantRequestPolicyAttribute>();
            var parameters = action.GetParameters().Where(p => typeof(IBaseRequest).IsAssignableFrom(p.ParameterType)).ToArray();
            if (parameters.Length != 0) Assert.Equal(Assert.Single(parameters).ParameterType, metadata?.RequestType);
            if (metadata is null) continue;
            var policy = metadata.RequestType.GetCustomAttribute<RequiresTenantPermissionAttribute>()!;
            Assert.Equal(policy.Permission, metadata.Policy.Permission);
            Assert.Equal(policy.AssignedAlternative, metadata.Policy.AssignedAlternative);
            policies++;
        }
        Assert.True(policies > 0);
    }

    [Fact]
    public void Unauthenticated_subject_and_conflicting_or_duplicate_selection_fail_closed()
    {
        var http = new DefaultHttpContext(); var organisation = Guid.NewGuid();
        http.Request.Headers["X-Organisation-Id"] = organisation.ToString();
        var identity = new TenantRequestIdentity(new HttpContextAccessor { HttpContext = http });
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "forged") }));
        Assert.Empty(identity.SubjectId);
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "subject"), new Claim("org_id", Guid.NewGuid().ToString()) }, "validated"));
        Assert.Equal(Guid.Empty, identity.SelectedOrganisationId);
        http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "subject") }, "validated"));
        http.Request.Headers.Append("X-Organisation-Id", organisation.ToString());
        Assert.Equal(Guid.Empty, identity.SelectedOrganisationId);
    }

    [Theory]
    [InlineData(AccessFailure.Unauthenticated, 401)]
    [InlineData(AccessFailure.Denied, 403)]
    [InlineData(AccessFailure.Unavailable, 503)]
    public async Task Http_failure_translation_is_explicit(AccessFailure failure, int status)
    {
        var http = new DefaultHttpContext();
        await new TenantAccessMiddleware(_ => throw new TenantAccessException(failure)).InvokeAsync(http);
        Assert.Equal(status, http.Response.StatusCode);
    }

    [Theory]
    [InlineData(401, TenantAccessOutcome.Denied)] [InlineData(403, TenantAccessOutcome.Denied)]
    [InlineData(500, TenantAccessOutcome.Unavailable)] [InlineData(503, TenantAccessOutcome.Unavailable)]
    public async Task Adapter_has_no_success_fallback_for_failed_current_access(int status, TenantAccessOutcome expected)
    {
        using var http = new HttpClient(new ResponseHandler(_ => new((HttpStatusCode)status))) { BaseAddress = new("https://auth.invalid/") };
        var adapter = new CurrentTenantAccessClient(http, new Credential());
        Assert.Equal(expected, (await adapter.ResolveAsync("subject", Guid.NewGuid(), default)).Outcome);
    }

    [Theory]
    [InlineData("subject")] [InlineData("organisation")] [InlineData("version")] [InlineData("permission")]
    public async Task Adapter_rejects_conflicting_or_unknown_contract_values(string invalid)
    {
        var organisation = Guid.NewGuid();
        using var http = new HttpClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { ContractVersion = invalid == "version" ? 2 : 1,
                SubjectId = invalid == "subject" ? "other" : "subject",
                OrganisationId = invalid == "organisation" ? Guid.NewGuid() : organisation,
                OrganisationMembershipId = Guid.NewGuid(), DecisionVersion = "1", ObservedAtUtc = DateTimeOffset.UtcNow,
                EffectivePermissionCodes = new[] { invalid == "permission" ? "unknown" : "ReferenceData.View" } })
        })) { BaseAddress = new("https://auth.invalid/") };
        Assert.Equal(TenantAccessOutcome.Denied,
            (await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("subject", organisation, default)).Outcome);
    }

    [Fact]
    public async Task Deadline_is_bounded_and_does_not_retry()
    {
        var handler = new TimeoutHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://auth.invalid/") };
        var started = System.Diagnostics.Stopwatch.StartNew();
        var decision = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("subject", Guid.NewGuid(), default);
        Assert.Equal(TenantAccessOutcome.Unavailable, decision.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.InRange(started.Elapsed.TotalSeconds, 1, 5);
    }
    private sealed class Credential : ITenantAccessCredential { public string BearerToken => "synthetic-test-token"; }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }
}

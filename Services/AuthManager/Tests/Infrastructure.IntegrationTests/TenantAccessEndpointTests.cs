using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authorization;
using AuthManager.Application.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.IntegrationTests;

public sealed class TenantAccessEndpointTests
{
    [Fact]
    public async Task Tenant_contract_uses_bearer_even_when_identity_cookie_is_the_default_scheme()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var fixture = new Zeka.Authentication.Tests.AuthenticationFixture();
        builder.Configuration.AddConfiguration(fixture.Configuration());
        builder.Services.AddAuthentication(options =>
        { options.DefaultAuthenticateScheme = "identity-cookie"; options.DefaultChallengeScheme = "identity-cookie"; })
            .AddCookie("identity-cookie");
        builder.Services.AddTenantAuthentication(fixture.Configuration());
        builder.Services.AddSingleton<Zeka.Authentication.IJwksDocumentSource>(new Zeka.Authentication.Tests.DocumentSource(fixture.Jwks()));
        var access = new Access();
        builder.Services.AddSingleton<ICurrentTenantAccess>(access);
        builder.Services.AddSingleton<IActiveOrganisationMembership, Membership>();
        var session = new Session();
        builder.Services.AddSingleton<IBrowserSessions>(session);
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization(); app.MapTenantAccess();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        var path = $"api/v1/tenant-access/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path)).StatusCode);
        var issuer = IssuerContractTests.Create(fixture);
        issuer.ConfirmPublication(await issuer.PrepareAsync());
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await issuer.IssueAsync(Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path)).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await issuer.IssueSessionAsync(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(5), default));
        using var authorized = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode); Assert.True(authorized.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.NoContent, (await http.GetAsync(path + $"/memberships/{Guid.NewGuid()}?includeInactive=true")).StatusCode);
        access.Outcome = TenantAccessOutcome.Denied;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(path)).StatusCode);
        access.Outcome = TenantAccessOutcome.Unavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(path)).StatusCode);
        session.Outcome = BrowserSessionOutcome.Unauthenticated;
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path)).StatusCode);
        session.Outcome = BrowserSessionOutcome.Unavailable;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(path)).StatusCode);
    }
    private sealed class Session : IBrowserSessions
    {
        public BrowserSessionOutcome Outcome { get; set; } = BrowserSessionOutcome.Success;
        public Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(Outcome);
        public Task<BrowserSessionResult> SignInAsync(string email, string password, string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Access : ICurrentTenantAccess
    {
        public TenantAccessOutcome Outcome { get; set; } = TenantAccessOutcome.Authorized;
        public Task<CurrentTenantAccess> ResolveAsync(Guid subject, Guid organisation, CancellationToken cancellationToken = default)
            => Task.FromResult(new CurrentTenantAccess(Outcome, organisation, Guid.NewGuid(),
                new[] { "TeamConfiguration.ManageStaffProfiles" }, "1", DateTimeOffset.UtcNow));
    }
    private sealed class Membership : IActiveOrganisationMembership
    {
        public Task<bool> ExistsAsync(Guid organisation, Guid membership, CancellationToken cancellationToken, bool requireActive = true)
            => Task.FromResult(!requireActive);
    }
}

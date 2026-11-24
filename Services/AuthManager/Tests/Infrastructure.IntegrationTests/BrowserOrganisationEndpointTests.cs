using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authentication;
using AuthManager.Application.Authorization;
using AuthManager.Application.Provisioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zeka.Authentication.Tests;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// Native HTTPS, real RS256/JWKS validation and framework antiforgery transport proof.
/// Application doubles deliberately do not establish PostgreSQL or browser-journey evidence.
/// </summary>
[Trait("Issue", "ZEK-50")]
public sealed class BrowserOrganisationEndpointTests
{
    private const string CreationPath = "/api/v1/browser-session/organisations";
    private const string Name = "Fictional organisation";

    [Fact]
    public async Task Authenticated_antiforgery_uses_bearer_principal_with_identity_cookie_defaults()
    {
        await using var host = await Host.StartAsync();
        Assert.Equal("identity-cookie", host.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value.DefaultAuthenticateScheme);
        var token = host.ValidToken();
        using var response = await host.CreateAsync(token, await host.CsrfAsync(token),
            "{\"displayName\":\"  Fictional organisation  \"}");
        await AssertSuccessAsync(response, host);
        Assert.Equal(1, host.Creation.Calls);
        Assert.Equal(host.Subject, host.Creation.Subject);
        Assert.Equal(host.Session, host.Creation.Session);
        Assert.Equal(Name, host.Creation.Command!.DisplayName);
        Assert.Null(host.Creation.Command.Slug);
        Assert.Null(host.Creation.Command.Agreements);
        Assert.Equal(host.Key.ToString("D"), host.Creation.Command.IdempotencyKey);
    }

    [Fact]
    public async Task Anonymous_antiforgery_cannot_authorise_authenticated_creation()
    {
        await using var host = await Host.StartAsync();
        using var response = await host.CreateAsync(host.ValidToken(), await host.CsrfAsync());
        await AssertErrorAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(0, host.Creation.Calls);
    }

    [Fact]
    public async Task Another_subjects_antiforgery_cannot_be_borrowed()
    {
        await using var host = await Host.StartAsync();
        var csrf = await host.CsrfAsync(host.ValidToken());
        using var response = await host.CreateAsync(host.OtherToken(), csrf);
        await AssertErrorAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(0, host.Creation.Calls);
    }

    [Theory]
    [InlineData("missing-sub")] [InlineData("missing-sid")] [InlineData("empty-sub")]
    [InlineData("empty-sid")] [InlineData("invalid-sub")] [InlineData("invalid-sid")]
    [InlineData("duplicate-sub")] [InlineData("duplicate-sid")] [InlineData("alternate-sub")]
    public async Task Malformed_identity_is_rejected_before_csrf_or_mutation(string shape)
    {
        await using var host = await Host.StartAsync();
        var token = host.Token(Claims(host, shape));
        using (var csrf = await host.GetCsrfAsync(token)) await AssertErrorAsync(csrf, HttpStatusCode.Unauthorized);
        using (var creation = await host.CreateAsync(token, "unusable-unit-csrf"))
            await AssertErrorAsync(creation, HttpStatusCode.Unauthorized);
        using (var access = await host.AccessAsync(token, host.Organisation))
            await AssertErrorAsync(access, HttpStatusCode.Unauthorized);
        Assert.Equal(0, host.Creation.Calls);
        Assert.Equal(0, host.Access.Calls);
    }

    [Fact]
    public async Task Missing_expired_and_wrong_signature_bearers_never_reach_creation()
    {
        await using var host = await Host.StartAsync();
        using var forged = RSA.Create(2048);
        foreach (var token in new string?[] { null, host.Token(Claims(host), expired: true), host.Token(Claims(host), signingKey: forged) })
        {
            using var response = await host.CreateAsync(token, "unusable-unit-csrf");
            await AssertErrorAsync(response, HttpStatusCode.Unauthorized);
        }
        Assert.Equal(0, host.Creation.Calls);
    }

    [Theory]
    [InlineData("missing-origin")] [InlineData("wrong-origin")] [InlineData("duplicate-origin")]
    [InlineData("missing-csrf")] [InlineData("wrong-csrf")] [InlineData("wrong-content-type")]
    public async Task Origin_json_and_framework_csrf_controls_are_preserved(string failure)
    {
        await using var host = await Host.StartAsync();
        var token = host.ValidToken();
        using var request = host.CreateRequest(token, await host.CsrfAsync(token));
        if (failure == "missing-origin") request.Headers.Remove("Origin");
        if (failure == "wrong-origin") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://untrusted.example.invalid"); }
        if (failure == "duplicate-origin") request.Headers.TryAddWithoutValidation("Origin", "https://untrusted.example.invalid");
        if (failure == "missing-csrf") request.Headers.Remove("X-Zeka-CSRF");
        if (failure == "wrong-csrf") { request.Headers.Remove("X-Zeka-CSRF"); request.Headers.Add("X-Zeka-CSRF", "unusable-unit-csrf"); }
        if (failure == "wrong-content-type") request.Content!.Headers.ContentType = new("text/plain");
        using var response = await host.Http.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal(0, host.Creation.Calls);
    }

    [Theory]
    [InlineData("missing")] [InlineData("empty")] [InlineData("invalid")]
    [InlineData("compact")] [InlineData("empty-guid")] [InlineData("multiple")]
    public async Task Exactly_one_nonempty_canonical_idempotency_key_is_required(string failure)
    {
        await using var host = await Host.StartAsync();
        var token = host.ValidToken();
        using var request = host.CreateRequest(token, await host.CsrfAsync(token));
        request.Headers.Remove("Idempotency-Key");
        if (failure != "missing") request.Headers.TryAddWithoutValidation("Idempotency-Key", failure switch
        {
            "empty" => [""], "invalid" => ["not-a-guid"], "compact" => [host.Key.ToString("N")],
            "empty-guid" => [Guid.Empty.ToString("D")], _ => [host.Key.ToString("D"), Guid.NewGuid().ToString("D")]
        });
        using var response = await host.Http.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "ORGANISATION_CREATION_REQUEST_INVALID");
        Assert.Equal(0, host.Creation.Calls);
    }

    [Theory]
    [InlineData("{}")] [InlineData("null")] [InlineData("[]")]
    [InlineData("{\"displayName\":null}")] [InlineData("{\"displayName\":7}")]
    [InlineData("{\"displayName\":\"   \"}")] [InlineData("{\"DisplayName\":\"Fictional\"}")]
    [InlineData("{\"displayName\":\"First\",\"displayName\":\"Second\"}")]
    [InlineData("{\"displayName\":\"Fictional\",\"subjectId\":\"forged\"}")]
    [InlineData("{\"displayName\":\"Fictional\",\"agreements\":[]}")]
    [InlineData("{\"displayName\":\"Fictional\",\"timeZoneId\":\"Europe/Brussels\"}")]
    [InlineData("{\"displayName\":{\"nested\":{\"tooDeep\":true}}}")]
    public async Task Only_one_display_name_is_accepted_and_authority_fields_cannot_be_supplied(string body)
    {
        await using var host = await Host.StartAsync();
        var token = host.ValidToken();
        using var response = await host.CreateAsync(token, await host.CsrfAsync(token), body);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "ORGANISATION_CREATION_REQUEST_INVALID");
        Assert.Equal(0, host.Creation.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Oversized_body_is_rejected_for_declared_and_chunked_content(bool chunked)
    {
        await using var host = await Host.StartAsync();
        var token = host.ValidToken();
        using var request = host.CreateRequest(token, await host.CsrfAsync(token),
            "{\"displayName\":\"" + new string('x', 5000) + "\"}");
        if (chunked)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync();
            request.Content.Dispose();
            request.Content = new UnknownLengthJson(bytes);
        }
        using var response = await host.Http.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "ORGANISATION_CREATION_REQUEST_INVALID");
        Assert.Equal(0, host.Creation.Calls);
    }

    [Fact]
    public async Task Overlong_name_is_rejected_even_in_a_small_json_body()
    {
        await using var host = await Host.StartAsync();
        var token = host.ValidToken();
        using var response = await host.CreateAsync(token, await host.CsrfAsync(token),
            JsonSerializer.Serialize(new { displayName = new string('x', 201) }));
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "ORGANISATION_CREATION_REQUEST_INVALID");
        Assert.Equal(0, host.Creation.Calls);
    }


    [Fact]
    public async Task Created_and_replayed_results_share_the_exact201_contract_without_cookies()
    {
        await using var host = await Host.StartAsync();
        var token = host.ValidToken(); var csrf = await host.CsrfAsync(token);
        using var created = await host.CreateAsync(token, csrf);
        await AssertSuccessAsync(created, host);
        var original = await created.Content.ReadAsStringAsync();
        host.Creation.Outcome = OrganisationProvisioningOutcome.Replay;
        using var replay = await host.CreateAsync(token, csrf);
        await AssertSuccessAsync(replay, host);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        Assert.Equal(2, host.Creation.Calls);
    }

    [Theory]
    [InlineData("conflict", 409, "ORGANISATION_IDEMPOTENCY_CONFLICT")]
    [InlineData("processing", 409, "ORGANISATION_CREATION_IN_PROGRESS")]
    [InlineData("limit", 409, "ORGANISATION_OWNERSHIP_LIMIT_REACHED")]
    [InlineData("inactive", 401, null)]
    [InlineData("database", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    [InlineData("unavailable", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    [InlineData("uncertain", 503, "ORGANISATION_CREATION_RESULT_UNCERTAIN")]
    public async Task Business_and_dependency_outcomes_are_generic_named_no_store_and_cookie_free(string failure, int status, string? code)
    {
        await using var host = await Host.StartAsync(); var token = host.ValidToken();
        if (failure == "conflict") host.Creation.Outcome = OrganisationProvisioningOutcome.Conflict;
        else if (failure == "processing") host.Creation.Outcome = OrganisationProvisioningOutcome.Processing;
        else host.Creation.Failure = failure;
        using var response = await host.CreateAsync(token, await host.CsrfAsync(token));
        await AssertErrorAsync(response, (HttpStatusCode)status, code);
        Assert.Equal(1, host.Creation.Calls);
    }

    [Theory]
    [InlineData("wrong-subject")] [InlineData("empty-organisation")] [InlineData("empty-membership")]
    public async Task Malformed_trusted_creation_result_is_never_relabelled_as_success(string failure)
    {
        await using var host = await Host.StartAsync(); var token = host.ValidToken(); host.Creation.Failure = failure;
        using var response = await host.CreateAsync(token, await host.CsrfAsync(token));
        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "AUTH_DEPENDENCY_UNAVAILABLE");
    }

    [Theory]
    [InlineData("Forwarded")] [InlineData("X-Forwarded-Proto")] [InlineData("X-Forwarded-Host")]
    [InlineData("X-Original-For")] [InlineData("X-Forwarded-Prefix")]
    public async Task Forged_forwarding_metadata_is_rejected_before_creation(string header)
    {
        await using var host = await Host.StartAsync(); var token = host.ValidToken();
        using var request = host.CreateRequest(token, await host.CsrfAsync(token));
        request.Headers.TryAddWithoutValidation(header, "untrusted-unit-value");
        using var response = await host.Http.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(0, host.Creation.Calls);
    }

    [Fact]
    public async Task Other_user_and_unknown_organisation_receive_identical_non_disclosing404()
    {
        await using var host = await Host.StartAsync(); host.Access.DenyOtherSubjects = true;
        using var existing = await host.AccessAsync(host.OtherToken(), host.Organisation);
        using var absent = await host.AccessAsync(host.OtherToken(), Guid.NewGuid());
        await AssertErrorAsync(existing, HttpStatusCode.NotFound);
        await AssertErrorAsync(absent, HttpStatusCode.NotFound);
        Assert.Equal(await existing.Content.ReadAsStringAsync(), await absent.Content.ReadAsStringAsync());
        Assert.Equal(host.OtherSubject, host.Access.Subject);
        Assert.Equal(host.OtherSession, host.Sessions.Session);
    }

    [Fact]
    public async Task Selected_access_returns_current_metadata_and_unset_timezone_without_cookie()
    {
        await using var host = await Host.StartAsync();
        using var response = await host.AccessAsync(host.ValidToken(), host.Organisation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(new[] { "contractVersion", "subjectId", "organisationId", "membershipId", "permissions", "authorizationVersion",
            "observedAtUtc", "displayName", "timeZoneId", "timeZoneConfigVersion" }, root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(host.Subject, root.GetProperty("subjectId").GetGuid());
        Assert.Equal(host.Organisation, root.GetProperty("organisationId").GetGuid());
        Assert.Equal(host.Membership, root.GetProperty("membershipId").GetGuid());
        Assert.Equal(new[] { "Organisations.Close", "Organisations.Export" },
            root.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("unit-current-version", root.GetProperty("authorizationVersion").GetString());
        Assert.Equal(Name, root.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("timeZoneId").ValueKind);
        Assert.Equal(0, root.GetProperty("timeZoneConfigVersion").GetInt64());
        Assert.Equal(host.Subject, host.Sessions.Subject);
        Assert.Equal(host.Session, host.Sessions.Session);
        Assert.Equal(host.Organisation, host.Access.Organisation);
    }

    [Theory]
    [InlineData("stale-session", 401, null)]
    [InlineData("session-unavailable", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    [InlineData("denied", 404, null)]
    [InlineData("access-unavailable", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    [InlineData("wrong-organisation", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    [InlineData("empty-membership", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    [InlineData("empty-name", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    [InlineData("empty-version", 503, "AUTH_DEPENDENCY_UNAVAILABLE")]
    public async Task Selected_access_fails_closed_on_stale_session_denial_or_inconsistent_authority(string failure, int status, string? code)
    {
        await using var host = await Host.StartAsync();
        if (failure == "stale-session") host.Sessions.Outcome = BrowserSessionOutcome.Unauthenticated;
        else if (failure == "session-unavailable") host.Sessions.Outcome = BrowserSessionOutcome.Unavailable;
        else host.Access.Failure = failure;
        using var response = await host.AccessAsync(host.ValidToken(), host.Organisation);
        await AssertErrorAsync(response, (HttpStatusCode)status, code);
        if (failure is "stale-session" or "session-unavailable") Assert.Equal(0, host.Access.Calls);
    }

    [Theory]
    [InlineData("query")] [InlineData("selector")]
    public async Task Selected_route_rejects_secondary_tenant_selectors(string input)
    {
        await using var host = await Host.StartAsync();
        using var request = host.Request(HttpMethod.Get,
            $"/api/v1/browser-organisations/{host.Organisation:D}/access" + (input == "query" ? "?tenant=forged" : ""), host.ValidToken());
        if (input == "selector") request.Headers.TryAddWithoutValidation("X-Organisation-Id", Guid.NewGuid().ToString("D"));
        using var response = await host.Http.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "ORGANISATION_CREATION_REQUEST_INVALID");
        Assert.Equal(0, host.Access.Calls);
    }

    [Fact]
    public async Task Native_https_requires_the_generated_root_and_correct_server_name()
    {
        await using var host = await Host.StartAsync();
        using var untrusted = host.Client(trustRoot: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync("/api/v1/browser-session/csrf"));
        using var wrongName = host.Client(wrongName: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongName.GetAsync("/api/v1/browser-session/csrf"));
        Assert.Equal(0, host.Creation.Calls);
    }

    private static Claim[] Claims(Host host, string shape = "valid")
    {
        var claims = new List<Claim>();
        if (shape != "missing-sub")
            claims.Add(new("sub", shape == "empty-sub" ? Guid.Empty.ToString("D") : shape == "invalid-sub" ? "not-a-guid" : host.Subject.ToString("D")));
        if (shape != "missing-sid")
            claims.Add(new("sid", shape == "empty-sid" ? Guid.Empty.ToString("D") : shape == "invalid-sid" ? "not-a-guid" : host.Session.ToString("D")));
        if (shape == "duplicate-sub") claims.Add(new("sub", host.OtherSubject.ToString("D")));
        if (shape == "duplicate-sid") claims.Add(new("sid", host.OtherSession.ToString("D")));
        if (shape == "alternate-sub") claims.Add(new(ClaimTypes.NameIdentifier, host.OtherSubject.ToString("D")));
        return claims.ToArray();
    }

    private static async Task AssertSuccessAsync(HttpResponseMessage response, Host host)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = document.RootElement;
        Assert.Equal(new[] { "contractVersion", "subjectId", "organisationId", "membershipId" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(host.Subject, root.GetProperty("subjectId").GetGuid());
        Assert.Equal(host.Organisation, root.GetProperty("organisationId").GetGuid());
        Assert.Equal(host.Membership, root.GetProperty("membershipId").GetGuid());
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-unit-detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", body, StringComparison.Ordinal);
        if (code is null) Assert.Equal("", body);
        else { using var document = JsonDocument.Parse(body); Assert.Equal(code, document.RootElement.GetProperty("code").GetString()); }
    }

    private sealed class Creation(HostIdentity identity) : IBrowserOrganisationCreation
    {
        internal int Calls { get; private set; }
        internal Guid Subject { get; private set; }
        internal Guid Session { get; private set; }
        internal OrganisationProvisioningCommand? Command { get; private set; }
        internal OrganisationProvisioningOutcome Outcome { get; set; } = OrganisationProvisioningOutcome.Created;
        internal string? Failure { get; set; }
        public Task<OrganisationProvisioningResult> CreateAsync(Guid subjectId, Guid sessionId,
            OrganisationProvisioningCommand command, CancellationToken cancellationToken = default)
        {
            Calls++; Subject = subjectId; Session = sessionId; Command = command;
            if (Failure == "inactive") throw new OrganisationProvisioningException("ORGANISATION_CREATION_UNAUTHENTICATED");
            if (Failure == "limit") throw new OrganisationProvisioningException("ORGANISATION_OWNERSHIP_LIMIT_REACHED");
            if (Failure == "database") throw new UnitDatabaseException();
            if (Failure == "unavailable") throw new OrganisationProvisioningException(BrowserOrganisationCreationCodes.Unavailable);
            if (Failure == "uncertain") throw new OrganisationProvisioningException(BrowserOrganisationCreationCodes.ResultUncertain);
            return Task.FromResult(new OrganisationProvisioningResult(Outcome,
                Failure == "wrong-subject" ? identity.OtherSubject : subjectId,
                Failure == "empty-organisation" ? Guid.Empty : identity.Organisation,
                Failure == "empty-membership" ? Guid.Empty : identity.Membership));
        }
    }

    private sealed class Sessions : IBrowserSessions
    {
        internal BrowserSessionOutcome Outcome { get; set; } = BrowserSessionOutcome.Success;
        internal Guid Subject { get; private set; }
        internal Guid Session { get; private set; }
        public Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken)
        { Subject = subjectId; Session = sessionId; return Task.FromResult(Outcome); }
        public Task<BrowserSessionResult> SignInAsync(string email, string password, string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Access(HostIdentity identity) : ICurrentTenantAccess
    {
        internal int Calls { get; private set; }
        internal Guid Subject { get; private set; }
        internal Guid Organisation { get; private set; }
        internal bool DenyOtherSubjects { get; set; }
        internal string? Failure { get; set; }
        public Task<CurrentTenantAccess> ResolveAsync(Guid subjectId, Guid selectedOrganisationId, CancellationToken cancellationToken = default)
        {
            Calls++; Subject = subjectId; Organisation = selectedOrganisationId;
            if (Failure == "denied" || DenyOtherSubjects && subjectId != identity.Subject)
                return Task.FromResult(CurrentTenantAccess.Denied(DateTimeOffset.UtcNow));
            return Task.FromResult(new CurrentTenantAccess(
                Failure == "access-unavailable" ? TenantAccessOutcome.Unavailable : TenantAccessOutcome.Authorized,
                Failure == "wrong-organisation" ? identity.OtherSubject : selectedOrganisationId,
                Failure == "empty-membership" ? Guid.Empty : identity.Membership,
                ["Organisations.Close", "Organisations.Export"],
                Failure == "empty-version" ? "" : "unit-current-version", DateTimeOffset.UtcNow)
            { DisplayName = Failure == "empty-name" ? "" : Name, TimeZoneId = null, TimeZoneConfigVersion = 0 });
        }
    }

    private sealed class UnitDatabaseException : DbException
    { internal UnitDatabaseException() : base("private-unit-detail") { } }

    private sealed class UnknownLengthJson : HttpContent
    {
        private readonly byte[] body;
        internal UnknownLengthJson(byte[] body)
        { this.body = body; Headers.ContentType = new("application/json"); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class HostIdentity
    {
        internal Guid Subject { get; } = Guid.NewGuid();
        internal Guid Session { get; } = Guid.NewGuid();
        internal Guid OtherSubject { get; } = Guid.NewGuid();
        internal Guid OtherSession { get; } = Guid.NewGuid();
        internal Guid Organisation { get; } = Guid.NewGuid();
        internal Guid Membership { get; } = Guid.NewGuid();
    }


    private sealed class Host(WebApplication app, AuthenticationFixture auth, RSA caKey, RSA serverKey,
        X509Certificate2 root, X509Certificate2 certificate, Uri address, HostIdentity identity,
        Creation creation, Sessions sessions, Access access) : IAsyncDisposable
    {
        private const string Origin = "https://app.example.invalid";
        internal Guid Subject => identity.Subject;
        internal Guid Session => identity.Session;
        internal Guid OtherSubject => identity.OtherSubject;
        internal Guid OtherSession => identity.OtherSession;
        internal Guid Organisation => identity.Organisation;
        internal Guid Membership => identity.Membership;
        internal Guid Key { get; } = Guid.NewGuid();
        internal Creation Creation => creation;
        internal Sessions Sessions => sessions;
        internal Access Access => access;
        internal IServiceProvider Services => app.Services;
        private HttpClient? http;
        internal HttpClient Http => http ??= Client();

        internal static async Task<Host> StartAsync()
        {
            AuthenticationFixture? auth = null; RSA? caKey = null, serverKey = null;
            X509Certificate2? root = null, certificate = null; WebApplication? app = null;
            try
            {
                auth = new(); caKey = RSA.Create(2048); serverKey = RSA.Create(2048);
                var now = DateTimeOffset.UtcNow;
                var caRequest = new CertificateRequest("CN=Zeka ephemeral transport unit CA",
                    caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                root = caRequest.CreateSelfSigned(now.AddMinutes(-1), now.AddHours(1));
                var serverRequest = new CertificateRequest("CN=localhost",
                    serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
                serverRequest.CertificateExtensions.Add(names.Build());
                var serial = RandomNumberGenerator.GetBytes(16); serial[0] = (byte)((serial[0] & 0x7f) | 1);
                using var publicCertificate = serverRequest.Create(root, now.AddMinutes(-1), now.AddMinutes(30), serial);
                // Match the existing Windows HTTPS fixture import; temporary PFX bytes are cleared immediately.
                using var keyedCertificate = publicCertificate.CopyWithPrivateKey(serverKey);
                var fixturePfx = keyedCertificate.Export(X509ContentType.Pfx);
                try { certificate = new X509Certificate2(fixturePfx); }
                finally { CryptographicOperations.ZeroMemory(fixturePfx); }
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                    options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
                builder.Configuration.AddConfiguration(auth.Configuration());
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BrowserSession:Origin"] = Origin,
                    ["BrowserSession:TrustedProxyPeers:0"] = "127.0.0.1",
                    ["BrowserSession:Limits:PerAddress"] = "100",
                    ["BrowserSession:Limits:Aggregate"] = "1000",
                    ["BrowserSession:Limits:MaxPartitions"] = "16",
                    ["BrowserSession:Limits:WindowSeconds"] = "60"
                });
                builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
                // Mirror the existing Identity default before adding the real bearer validator.
                builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "identity-cookie";
                    options.DefaultChallengeScheme = "identity-cookie";
                }).AddCookie("identity-cookie");
                builder.Services.AddTenantAuthentication(auth.Configuration());
                builder.Services.AddSingleton<Zeka.Authentication.IJwksDocumentSource>(new DocumentSource(auth.Jwks()));
                builder.Services.AddSingleton<TimeProvider>(auth.Clock);
                builder.Services.AddProblemDetails();
                builder.Services.AddBrowserSessionTransport();
                var identity = new HostIdentity();
                var creation = new Creation(identity); var sessions = new Sessions(); var access = new Access(identity);
                builder.Services.AddSingleton<IBrowserOrganisationCreation>(creation);
                builder.Services.AddSingleton<IBrowserSessions>(sessions);
                builder.Services.AddSingleton<ICurrentTenantAccess>(access);
                app = builder.Build();
                app.UseBrowserSessionAttribution();
                app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
                app.MapBrowserSessions(); app.MapBrowserOrganisationAccess();
                await app.StartAsync();
                var address = new Uri(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                return new(app, auth, caKey, serverKey, root, certificate, address, identity, creation, sessions, access);
            }
            catch
            {
                await DisposeOwnedAsync(null, app, certificate, root, serverKey, caKey, auth, suppressErrors: true);
                throw;
            }
        }

        internal HttpClient Client(bool trustRoot = true, bool wrongName = false)
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationFlags = X509VerificationFlags.NoFlag
            };
            if (trustRoot) policy.CustomTrustStore.Add(root);
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            var handler = new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, UseCookies = true,
                CookieContainer = new CookieContainer(),
                SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = policy }
            };
            var target = new UriBuilder(address)
                { Host = wrongName ? "wrong-name.example.invalid" : "127.0.0.1" }.Uri;
            if (wrongName) handler.ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, address.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
            return new HttpClient(handler) { BaseAddress = target, Timeout = TimeSpan.FromSeconds(15) };
        }

        internal string ValidToken() => Token(Claims(this));
        internal string OtherToken() => Token(
            [new("sub", OtherSubject.ToString("D")), new("sid", OtherSession.ToString("D"))]);

        internal string Token(IEnumerable<Claim> claims, bool expired = false, RSA? signingKey = null)
        {
            var jwt = new JwtSecurityToken(AuthenticationFixture.Issuer, AuthenticationFixture.Audience, claims,
                DateTime.UtcNow.AddHours(-2), expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(new RsaSecurityKey(signingKey ?? auth.Key)
                    { KeyId = AuthenticationFixture.KeyId }, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }

        internal HttpRequestMessage Request(HttpMethod method, string path, string? token)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Forwarded-For", "198.51.100.73");
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            return request;
        }

        internal HttpRequestMessage CreateRequest(string? token, string? csrf,
            string body = "{\"displayName\":\"Fictional organisation\"}")
        {
            var request = Request(HttpMethod.Post, CreationPath, token);
            request.Headers.Add("Origin", Origin);
            if (csrf is not null) request.Headers.Add("X-Zeka-CSRF", csrf);
            request.Headers.Add("Idempotency-Key", Key.ToString("D"));
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }

        internal async Task<HttpResponseMessage> CreateAsync(string? token, string? csrf,
            string body = "{\"displayName\":\"Fictional organisation\"}")
        {
            using var request = CreateRequest(token, csrf, body);
            return await Http.SendAsync(request);
        }

        internal async Task<HttpResponseMessage> GetCsrfAsync(string? token = null)
        {
            using var request = Request(HttpMethod.Get, "/api/v1/browser-session/csrf", token);
            return await Http.SendAsync(request);
        }

        internal async Task<string> CsrfAsync(string? token = null)
        {
            using var response = await GetCsrfAsync(token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("csrfToken").GetString()!;
        }

        internal async Task<HttpResponseMessage> AccessAsync(string token, Guid organisation)
        {
            using var request = Request(HttpMethod.Get, $"/api/v1/browser-organisations/{organisation:D}/access", token);
            return await Http.SendAsync(request);
        }

        private bool disposed;
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await DisposeOwnedAsync(http, app, certificate, root, serverKey, caKey, auth, suppressErrors: false);
        }

        private static async Task DisposeOwnedAsync(HttpClient? client, WebApplication? application,
            X509Certificate2? certificate, X509Certificate2? root, RSA? serverKey, RSA? caKey,
            AuthenticationFixture? authentication, bool suppressErrors)
        {
            Exception? first = null;
            void Release(IDisposable? resource) { try { resource?.Dispose(); } catch (Exception error) { first ??= error; } }
            Release(client);
            try { if (application is not null) await application.DisposeAsync(); }
            catch (Exception error) { first ??= error; }
            Release(certificate); Release(root); Release(serverKey); Release(caKey); Release(authentication);
            if (!suppressErrors && first is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }
}

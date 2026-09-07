using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClientManagement.API.Controllers;
using ClientManagement.Application.Clients.Commands.UpdateNativeLanguage;
using ClientManagement.Application.Clients.Queries.GetClients;
using ClientManagement.Core.ValueObjects;
using ClientManagement.Tests.Common;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Application.IntegrationTests;

public sealed class ClientApiContractTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // Test diagnostics must never record request values.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddControllers().AddApplicationPart(typeof(ClientsController).Assembly)
            .AddNewtonsoftJson();
        builder.Services.Configure<ApiBehaviorOptions>(options => options.SuppressModelStateInvalidFilter = true);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(ClientApiContractTests).Assembly));
        _app = builder.Build();
        _app.UseSwagger();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapControllers();
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        _http = new HttpClient { BaseAddress = new Uri(address) };
        _http.DefaultRequestHeaders.Add("X-Test-User", "synthetic-user");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Search_binds_body_and_never_allows_caching()
    {
        using var response = await _http.PostAsJsonAsync("/api/clients/search",
            new { searchText = SyntheticClient.Niss() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Native_language_binds_patch_body_and_rejects_noncanonical_niss_safely()
    {
        var niss = SyntheticClient.Niss();
        using var valid = await _http.PatchAsJsonAsync("/api/clients/native-language", new { niss, language = "Français" });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        using var invalid = await _http.PatchAsJsonAsync("/api/clients/native-language",
            new { niss = " " + niss, language = "Français" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.False(body.Contains(niss), "Error response must not echo the submitted identifier.");
    }

    [Theory]
    [InlineData("search")]
    [InlineData("native-language")]
    public async Task Missing_or_malformed_body_is_rejected_without_echoing_values(string route)
    {
        var niss = SyntheticClient.Niss();
        var payloads = new[] { "", "null", "{}", "{\"niss\":[\"" + niss + "\"],\"searchText\":[\"" + niss + "\"]}" };
        foreach (var payload in payloads)
        {
            using var request = new HttpRequestMessage(route == "search" ? HttpMethod.Post : HttpMethod.Patch,
                "/api/clients/" + route)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
            using var response = await _http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False((await response.Content.ReadAsStringAsync()).Contains(niss), "Binding errors must not echo input.");
            if (route == "search") Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Theory]
    [InlineData("search")]
    [InlineData("native-language")]
    public async Task Sensitive_routes_still_require_authentication(string route)
    {
        _http.DefaultRequestHeaders.Remove("X-Test-User");
        using var request = new HttpRequestMessage(route == "search" ? HttpMethod.Post : HttpMethod.Patch,
            "/api/clients/" + route) { Content = JsonContent.Create(new { }) };
        using var response = await _http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Legacy_routes_are_removed_and_OpenAPI_describes_body_only_contracts()
    {
        // Placeholders deliberately avoid sending even synthetic NISS values in URLs.
        using var search = await _http.GetAsync("/api/clients/searchtext/synthetic-placeholder");
        using var language = await _http.PostAsync("/api/clients/updatelanguage/synthetic-placeholder/Français", null);
        Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, language.StatusCode);

        using var response = await _http.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var searchOperation = paths.GetProperty("/api/Clients/search").GetProperty("post");
        var languageOperation = paths.GetProperty("/api/Clients/native-language").GetProperty("patch");
        AssertBodyOnly(searchOperation);
        AssertBodyOnly(languageOperation);
        Assert.All(paths.EnumerateObject(), path =>
        {
            Assert.False(path.Name.Contains("searchtext", StringComparison.OrdinalIgnoreCase));
            Assert.False(path.Name.Contains("updatelanguage", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static void AssertBodyOnly(JsonElement operation)
    {
        Assert.True(operation.TryGetProperty("requestBody", out var body));
        Assert.True(body.GetProperty("required").GetBoolean());
        Assert.True(body.GetProperty("content").TryGetProperty("application/json", out _));
        Assert.False(operation.TryGetProperty("parameters", out _));
    }

    // Substitute only application handlers; routing, MVC binding, filters, authentication
    // requirements, response headers and Swagger generation use the real controller.
    public sealed class SearchHandler : IRequestHandler<GetClientsBySearchTextQuery, ClientsDto>
    {
        public Task<ClientsDto> Handle(GetClientsBySearchTextQuery request, CancellationToken cancellationToken)
        {
            if (request.SearchText != SyntheticClient.Niss()) throw new InvalidOperationException("Body was not bound.");
            return Task.FromResult(new ClientsDto());
        }
    }

    public sealed class LanguageHandler : IRequestHandler<UpdateNativeLanguageCommand, int>
    {
        public Task<int> Handle(UpdateNativeLanguageCommand request, CancellationToken cancellationToken)
        {
            _ = Niss.Parse(request.Niss);
            if (request.Language != "Français") throw new InvalidOperationException("Body was not bound.");
            return Task.FromResult(1);
        }
    }

    private sealed class TestAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.ContainsKey("X-Test-User")
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
                    new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "synthetic-user") }, "test")), "test"))
                : AuthenticateResult.NoResult());
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Zeka.Authentication;

namespace Infrastructure.IntegrationTests;

public sealed class JwksHttpSourceTests
{
    [Theory]
    [InlineData(65536, false)]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    [InlineData(65537, true)]
    public async Task Response_size_limit_applies_with_and_without_content_length(int size, bool chunked)
    {
        var body = new string('x', size);
        await using var app = CreateHost();
        app.MapGet("/jwks", async context =>
        {
            if (!chunked) context.Response.ContentLength = size;
            await context.Response.StartAsync();
            await context.Response.WriteAsync(body);
        });
        await app.StartAsync();
        using var http = new HttpClient();
        var source = Source(app, http);

        if (size <= 65536)
            Assert.Equal(body, await source.ReadAsync(CancellationToken.None));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unsuccessful_http_status_is_rejected()
    {
        await using var app = CreateHost();
        app.MapGet("/jwks", () => Results.StatusCode(503));
        await app.StartAsync();
        using var http = new HttpClient();

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => Source(app, http).ReadAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    private static WebApplication CreateHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        return builder.Build();
    }

    private static HttpJwksDocumentSource Source(WebApplication app, HttpClient http)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        // Exercise the HTTP transport directly against a private loopback server.
        // Production configuration validation still requires HTTPS.
        return new HttpJwksDocumentSource(http, new AuthenticationOptions { JwksUri = address + "/jwks" });
    }
}

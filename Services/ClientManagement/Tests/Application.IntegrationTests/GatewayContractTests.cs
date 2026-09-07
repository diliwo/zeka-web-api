using System.Text.Json;
using Xunit;

namespace Application.IntegrationTests;

public sealed class GatewayContractTests
{
    [Theory]
    [InlineData("search", "Post")]
    [InlineData("native-language", "Patch")]
    public void Sensitive_client_routes_forward_only_the_agreed_method_without_gateway_caching(string suffix, string method)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GatewayRoutes.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var route = document.RootElement.GetProperty("Routes").EnumerateArray()
            .Single(candidate => candidate.GetProperty("UpstreamPathTemplate").GetString() == "/clients/" + suffix);

        Assert.Equal("/api/clients/" + suffix, route.GetProperty("DownstreamPathTemplate").GetString());
        Assert.Equal(method, route.GetProperty("UpstreamHttpMethod").EnumerateArray().Single().GetString());
        Assert.True(route.GetProperty("RateLimitOptions").GetProperty("EnableRateLimiting").GetBoolean());
        Assert.False(route.TryGetProperty("FileCacheOptions", out _));
    }
}

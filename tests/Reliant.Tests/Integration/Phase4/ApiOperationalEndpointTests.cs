using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Reliant.Tests.Integration.Phase4;

[Trait("Category", "Phase4")]
[Trait("Category", "Integration")]
public sealed class ApiOperationalEndpointTests :
    IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiOperationalEndpointTests(
        WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LiveAndVersionEndpoints_ShouldExposeOperationalState()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/version");
        request.Headers.Add(
            "X-Correlation-ID",
            "phase4-endpoint-test");

        using var versionResponse = await _client.SendAsync(request);
        var version = await versionResponse.Content
            .ReadFromJsonAsync<VersionResponse>();
        var liveResponse = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
        Assert.Equal(
            "phase4-endpoint-test",
            versionResponse.Headers.GetValues(
                "X-Correlation-ID").Single());
        Assert.NotNull(version);
        Assert.Equal("Reliant.Api", version!.ServiceName);
        Assert.False(string.IsNullOrWhiteSpace(version.Version));
        Assert.False(string.IsNullOrWhiteSpace(version.Environment));
    }

    private sealed record VersionResponse(
        string ServiceName,
        string Version,
        string Environment,
        string Commit,
        string InstanceId);
}

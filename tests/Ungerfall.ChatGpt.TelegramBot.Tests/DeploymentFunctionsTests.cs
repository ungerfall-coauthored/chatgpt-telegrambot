using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.AzureFunction;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class DeploymentFunctionsTests
{
    [Fact]
    public async Task HealthReportsEmbeddedArtifactWithoutAnnouncementConfiguration()
    {
        var artifact = ReleaseAnnouncementSettings.ArtifactVersion;
        var (request, response) = Request();
        var function = new DeploymentFunctions();
        await function.Health(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(response.Body);
        Assert.Equal(artifact, body.RootElement.GetProperty("version").GetString());
    }

    private static (HttpRequestData, HttpResponseData) Request()
    {
        var context = Substitute.For<FunctionContext>();
        var request = Substitute.For<HttpRequestData>(context);
        var response = Substitute.For<HttpResponseData>(context);
        request.CreateResponse().Returns(response);
        response.Headers.Returns(new HttpHeadersCollection());
        response.Body.Returns(new MemoryStream());
        return (request, response);
    }
}

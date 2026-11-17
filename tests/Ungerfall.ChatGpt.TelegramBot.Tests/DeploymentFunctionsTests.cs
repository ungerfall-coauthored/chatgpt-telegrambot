using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.AzureFunction;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class DeploymentFunctionsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HealthUsesArtifactIdentity_NotOverriddenReleaseSetting(bool matchesArtifact)
    {
        var artifact = ReleaseAnnouncementSettings.ArtifactVersion;
        var settings = new ReleaseAnnouncementSettings(true, matchesArtifact ? artifact : new string('a', 40));
        var (request, response) = Request();
        var function = new DeploymentFunctions(settings, Substitute.For<IReleaseAnnouncementRepository>());
        await function.Health(request);
        Assert.Equal(matchesArtifact ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(response.Body);
        Assert.Equal(artifact, body.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ActivationRequiresCurrentVersionAndSafeSettings(bool enabled, bool approval, bool matchesArtifact)
    {
        var artifact = ReleaseAnnouncementSettings.ArtifactVersion;
        var settings = new ReleaseAnnouncementSettings(enabled, artifact, requireDeploymentApproval: approval);
        var repository = Substitute.For<IReleaseAnnouncementRepository>();
        var (request, response) = Request();
        var function = new DeploymentFunctions(settings, repository);
        var version = matchesArtifact ? artifact : new string('a', 40);
        await function.Activate(request, version, default);
        var valid = enabled && approval && matchesArtifact;
        Assert.Equal(valid ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, response.StatusCode);
        await repository.Received(valid ? 1 : 0).ApproveDeployment(version, Arg.Any<CancellationToken>());
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

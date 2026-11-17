using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;

public sealed class DeploymentFunctions(ReleaseAnnouncementSettings settings, IReleaseAnnouncementRepository repository)
{
    [Function("Health")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        var version = ReleaseAnnouncementSettings.ArtifactVersion;
        var valid = ReleaseAnnouncementSettings.IsCommit(version) && settings.Version == version;
        var response = request.CreateResponse(valid ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            status = valid ? "ready" : "unready",
            version,
            announcementsEnabled = settings.Enabled,
            requiresApproval = settings.RequireDeploymentApproval,
        }));
        return response;
    }

    [Function("ActivateRelease")]
    public async Task<HttpResponseData> Activate(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "releases/{version}/activate")] HttpRequestData request,
        string version, CancellationToken cancellation)
    {
        if (!settings.Enabled || !settings.RequireDeploymentApproval
            || !ReleaseAnnouncementSettings.IsCommit(version)
            || !string.Equals(version, ReleaseAnnouncementSettings.ArtifactVersion, StringComparison.Ordinal)
            || settings.Version != version)
            return request.CreateResponse(HttpStatusCode.Conflict);
        await repository.ApproveDeployment(version, cancellation);
        return request.CreateResponse(HttpStatusCode.NoContent);
    }
}
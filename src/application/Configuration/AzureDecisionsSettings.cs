using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public sealed class AzureDecisionsSettings
{
    public AzureDecisionsSettings(string? endpoint = null, string? apiKey = null, string? deployment = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(deployment))
            return;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            throw new ArgumentException("AZURE_DECISIONS_ENDPOINT, AZURE_DECISIONS_API_KEY, and AZURE_DECISIONS_DEPLOYMENT must all be configured.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("AZURE_DECISIONS_ENDPOINT must be an HTTPS resource endpoint without a path or credentials.");
        Endpoint = uri;
        ApiKey = apiKey;
        Deployment = deployment;
    }

    private Uri? Endpoint { get; }
    private string? ApiKey { get; }
    public string? Deployment { get; }
    public bool IsConfigured => Deployment != null;

    public static AzureDecisionsSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("AZURE_DECISIONS_ENDPOINT"),
        Environment.GetEnvironmentVariable("AZURE_DECISIONS_API_KEY"),
        Environment.GetEnvironmentVariable("AZURE_DECISIONS_DEPLOYMENT"));

    public HttpRequestMessage CreateRequest()
    {
        if (!IsConfigured) throw new InvalidOperationException("Azure Decisions is not configured.");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint!, "providers/microsoft/v1/systemone"));
        request.Headers.Add("api-key", ApiKey);
        return request;
    }
}

public static class AzureDecisionsServiceCollectionExtensions
{
    public static IServiceCollection AddAzureDecisions(this IServiceCollection services)
    {
        services.AddSingleton(_ => AzureDecisionsSettings.FromEnvironment());
        services.AddHttpClient<DecisionsClient>(client => client.MaxResponseContentBufferSize = 32768)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(new[] { "api-key" });
        return services;
    }
}

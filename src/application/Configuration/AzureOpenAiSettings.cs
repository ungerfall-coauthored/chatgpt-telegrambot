using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Interfaces;
using OpenAI.Managers;
using System;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public sealed class AzureOpenAiSettings
{
    public AzureOpenAiSettings(string endpoint, string apiKey, string chatDeployment, string? summaryDeployment, string? imageDeployment, string apiVersion, string imageApiVersion = "2025-04-01-preview")
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("AZURE_OPENAI_ENDPOINT must be an HTTPS resource endpoint without a path or credentials.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("AZURE_OPENAI_API_KEY is missing.");
        if (string.IsNullOrWhiteSpace(chatDeployment)) throw new ArgumentException("AZURE_OPENAI_CHAT_DEPLOYMENT is missing.");
        if (string.IsNullOrWhiteSpace(apiVersion)) throw new ArgumentException("AZURE_OPENAI_API_VERSION is empty.");
        if (string.IsNullOrWhiteSpace(imageApiVersion)) throw new ArgumentException("AZURE_OPENAI_IMAGE_API_VERSION is empty.");
        Endpoint = uri.GetLeftPart(UriPartial.Authority) + "/";
        ApiKey = apiKey;
        ChatDeployment = chatDeployment;
        SummaryDeployment = string.IsNullOrWhiteSpace(summaryDeployment) ? chatDeployment : summaryDeployment;
        ImageDeployment = string.IsNullOrWhiteSpace(imageDeployment) ? null : imageDeployment;
        ApiVersion = apiVersion;
        ImageApiVersion = imageApiVersion;
    }

    public string Endpoint { get; }
    private string ApiKey { get; }
    public string ChatDeployment { get; }
    public string SummaryDeployment { get; }
    public string? ImageDeployment { get; }
    public string ApiVersion { get; }
    public string ImageApiVersion { get; }

    public static AzureOpenAiSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_SUMMARY_DEPLOYMENT"),
        Environment.GetEnvironmentVariable("AZURE_OPENAI_IMAGE_DEPLOYMENT"),
        Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-10-21",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_IMAGE_API_VERSION") ?? "2025-04-01-preview");

    public OpenAiOptions CreateOptions(string deployment, bool image = false) => new()
    {
        ProviderType = ProviderType.Azure,
        BaseDomain = Endpoint,
        ApiKey = ApiKey,
        DeploymentId = deployment,
        DefaultModelId = deployment,
        ApiVersion = image ? ImageApiVersion : ApiVersion,
    };
}

public sealed class SummaryOpenAiClient : IDisposable
{
    public SummaryOpenAiClient(IOpenAIService service, string deployment) { Service = service; Deployment = deployment; }
    public IOpenAIService Service { get; }
    public string Deployment { get; }
    public void Dispose() => (Service as IDisposable)?.Dispose();
}

public sealed class ImageOpenAiClient : IDisposable
{
    public ImageOpenAiClient(IOpenAIService? service) => Service = service;
    public IOpenAIService? Service { get; }
    public void Dispose() => (Service as IDisposable)?.Dispose();
}

public static class AzureOpenAiServiceCollectionExtensions
{
    public static IServiceCollection AddAzureOpenAi(this IServiceCollection services)
    {
        var settings = AzureOpenAiSettings.FromEnvironment();
        services.AddSingleton(settings);
        services.AddSingleton<IOpenAIService>(_ => new OpenAIService(settings.CreateOptions(settings.ChatDeployment)));
        services.AddSingleton(_ => new SummaryOpenAiClient(
            new OpenAIService(settings.CreateOptions(settings.SummaryDeployment)), settings.SummaryDeployment));
        services.AddSingleton(_ => new ImageOpenAiClient(settings.ImageDeployment == null
            ? null : new OpenAIService(settings.CreateOptions(settings.ImageDeployment, image: true))));
        return services;
    }
}

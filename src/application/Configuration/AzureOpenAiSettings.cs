using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net.Http;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public sealed class AzureOpenAiSettings
{
    public AzureOpenAiSettings(string endpoint, string apiKey, string chatDeployment, string? summaryDeployment, string? imageDeployment, string imageApiVersion = "2025-04-01-preview",
        CompletionSettings? chat = null, CompletionSettings? summary = null, int? autonomousMaxCompletionTokens = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("AZURE_OPENAI_ENDPOINT must be an HTTPS resource endpoint without a path or credentials.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("AZURE_OPENAI_API_KEY is missing.");
        if (string.IsNullOrWhiteSpace(chatDeployment)) throw new ArgumentException("AZURE_OPENAI_CHAT_DEPLOYMENT is missing.");
        if (string.IsNullOrWhiteSpace(imageApiVersion)) throw new ArgumentException("AZURE_OPENAI_IMAGE_API_VERSION is empty.");
        Endpoint = uri.GetLeftPart(UriPartial.Authority) + "/";
        ApiKey = apiKey;
        ChatDeployment = chatDeployment;
        SummaryDeployment = string.IsNullOrWhiteSpace(summaryDeployment) ? chatDeployment : summaryDeployment;
        ImageDeployment = string.IsNullOrWhiteSpace(imageDeployment) ? null : imageDeployment;
        ImageApiVersion = imageApiVersion;
        Chat = chat ?? new CompletionSettings();
        Summary = summary ?? new CompletionSettings(inputTokens: CompletionSettings.DefaultSummaryInputTokens, temperature: .2f);
        AutonomousMaxCompletionTokens = autonomousMaxCompletionTokens ?? Math.Min(120, Chat.MaxCompletionTokens);
        if (AutonomousMaxCompletionTokens <= 0 || AutonomousMaxCompletionTokens > Chat.MaxCompletionTokens)
            throw new ArgumentOutOfRangeException(nameof(autonomousMaxCompletionTokens));
    }

    public string Endpoint { get; }
    private string ApiKey { get; }
    public string ChatDeployment { get; }
    public string SummaryDeployment { get; }
    public string? ImageDeployment { get; }
    public string ImageApiVersion { get; }
    public CompletionSettings Chat { get; }
    public CompletionSettings Summary { get; }
    public int AutonomousMaxCompletionTokens { get; }

    public static AzureOpenAiSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_SUMMARY_DEPLOYMENT"),
        Environment.GetEnvironmentVariable("AZURE_OPENAI_IMAGE_DEPLOYMENT"),
        Environment.GetEnvironmentVariable("AZURE_OPENAI_IMAGE_API_VERSION") ?? "2025-04-01-preview",
        CompletionSettings.FromEnvironment(),
        CompletionSettings.FromEnvironment(summary: true),
        Environment.GetEnvironmentVariable("AZURE_OPENAI_AUTONOMOUS_MAX_COMPLETION_TOKENS") is { } autonomousTokens
            ? int.Parse(autonomousTokens, CultureInfo.InvariantCulture) : null);

    public IChatCompletionClient CreateChatCompletionClient(string deployment, HttpClient? httpClient = null)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(new Uri(Endpoint), "openai/v1/"),
        };
        if (httpClient != null) options.Transport = new HttpClientPipelineTransport(httpClient);
        return new OpenAiChatCompletionClient(new ChatClient(deployment, new ApiKeyCredential(ApiKey), options));
    }

    public IImageGenerationClient CreateImageGenerationClient(HttpClient httpClient, string deployment) =>
        new AzureImageGenerationClient(httpClient, Endpoint, ApiKey, deployment, ImageApiVersion);

    public string ChatEndpoint => new Uri(new Uri(Endpoint), "openai/v1/").ToString();

}

public sealed class SummaryOpenAiClient : IDisposable
{
    public SummaryOpenAiClient(IChatCompletionClient client, string deployment, CompletionSettings? settings = null)
    {
        Client = client;
        Deployment = deployment;
        Settings = settings ?? new CompletionSettings(inputTokens: CompletionSettings.DefaultSummaryInputTokens, temperature: .2f);
    }
    public IChatCompletionClient Client { get; }
    public string Deployment { get; }
    public CompletionSettings Settings { get; }
    public void Dispose() => (Client as IDisposable)?.Dispose();
}

public sealed class ImageOpenAiClient : IDisposable
{
    public ImageOpenAiClient(IImageGenerationClient? client) => Client = client;
    public IImageGenerationClient? Client { get; }
    public void Dispose() => (Client as IDisposable)?.Dispose();
}

public static class AzureOpenAiServiceCollectionExtensions
{
    public static IServiceCollection AddAzureOpenAi(this IServiceCollection services)
    {
        var settings = AzureOpenAiSettings.FromEnvironment();
        services.AddSingleton(settings);
        services.AddSingleton<IChatCompletionClient>(_ => settings.CreateChatCompletionClient(settings.ChatDeployment));
        services.AddSingleton(_ => new SummaryOpenAiClient(
            settings.CreateChatCompletionClient(settings.SummaryDeployment), settings.SummaryDeployment, settings.Summary));
        services.AddHttpClient("azure-openai-images")
            .RedactLoggedHeaders(new[] { "api-key" });
        services.AddSingleton(sp => new ImageOpenAiClient(settings.ImageDeployment is { } deployment
            ? settings.CreateImageGenerationClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("azure-openai-images"), deployment)
            : null));
        return services;
    }
}

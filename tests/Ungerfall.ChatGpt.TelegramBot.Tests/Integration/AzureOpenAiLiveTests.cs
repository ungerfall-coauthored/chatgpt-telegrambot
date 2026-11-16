using OpenAI.Managers;
using OpenAI.ObjectModels.RequestModels;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
public class AzureOpenAiLiveTests
{
    [SkippableFact]
    public async Task DeployedModelsRespond()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var settings = new AzureOpenAiSettings(
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "",
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "",
            Environment.GetEnvironmentVariable("AZURE_OPENAI_CHAT_DEPLOYMENT") ?? "",
            Environment.GetEnvironmentVariable("AZURE_OPENAI_SUMMARY_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_IMAGE_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ?? "2024-10-21",
            Environment.GetEnvironmentVariable("AZURE_OPENAI_IMAGE_API_VERSION") ?? "2025-04-01-preview");

        foreach (var deployment in new[] { settings.ChatDeployment, settings.SummaryDeployment }.Distinct())
        {
            using var service = new OpenAIService(settings.CreateOptions(deployment));
            var result = await service.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
            {
                Model = deployment,
                Messages = [ChatMessage.FromUser("Reply with the word OK.")],
                Temperature = 0,
            }, deployment);
            Assert.True(result.Successful, $"Azure chat failed: {result.Error?.Type}");
            Assert.False(string.IsNullOrWhiteSpace(result.Choices?.FirstOrDefault()?.Message?.Content));
        }

        if (settings.ImageDeployment != null)
        {
            using var service = new OpenAIService(settings.CreateOptions(settings.ImageDeployment, image: true));
            var result = await service.Image.CreateImage(new ImageCreateRequest
            {
                Prompt = "A simple blue circle on a white background.",
                N = 1,
                Size = "1024x1024"
            });
            Assert.True(result.Successful, $"Azure image generation failed: {result.Error?.Type}");
            Assert.NotEmpty(Convert.FromBase64String(Assert.Single(result.Results).B64));
        }
    }
}

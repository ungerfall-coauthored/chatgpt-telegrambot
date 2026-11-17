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
        var settings = AzureOpenAiSettings.FromEnvironment();

        foreach (var (deployment, profile) in new[]
        {
            (settings.ChatDeployment, settings.Chat),
            (settings.SummaryDeployment, settings.Summary),
        })
        {
            using var service = new OpenAIService(settings.CreateOptions(deployment));
            var result = await service.ChatCompletion.CreateCompletion(
                profile.CreateRequest([ChatMessage.FromUser("Reply with the word OK.")], deployment), deployment);
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

using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Managers;
using OpenAI.ObjectModels.RequestModels;
using System.Net.Http.Json;
using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Conversation policy environment")]
public class AzureOpenAiLiveTests
{
    [SkippableFact]
    public async Task DecisionPolicyDoesNotAnswerMereBotDiscussion()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var settings = AzureDecisionsSettings.FromEnvironment();
        Skip.IfNot(settings.IsConfigured, "Azure Decisions deployment is not configured");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var scenario = new NaturalRequestScenario(new DecisionsClient(http, NullLogger<DecisionsClient>.Instance, settings));

        await scenario.Handler.Handle(scenario.Update(1, "Мы вчера обсуждали бота, но сейчас разговариваем между собой."), default);

        Assert.Equal(1, scenario.Budget.Decisions);
        await scenario.AssertNoReply();
    }

    [SkippableFact]
    public async Task DecisionPolicyAnswersRussianBotRequestAfterHumanInterruption()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var settings = AzureDecisionsSettings.FromEnvironment();
        Skip.IfNot(settings.IsConfigured, "Azure Decisions deployment is not configured");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var scenario = new NaturalRequestScenario(new DecisionsClient(http, NullLogger<DecisionsClient>.Instance, settings));

        await scenario.Run();

        await scenario.AssertOneReply();
    }

    [SkippableFact]
    public async Task DecisionClientAcceptsAnExplicitContinuation()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var settings = AzureDecisionsSettings.FromEnvironment();
        Skip.IfNot(settings.IsConfigured, "Azure Decisions deployment is not configured");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new DecisionsClient(http, NullLogger<DecisionsClient>.Instance, settings);
        var input = JsonSerializer.Serialize(new
        {
            context = new[]
            {
                new { speaker = "current_user", text = "Please explain how to multiply two fractions." },
                new { speaker = "bot", text = "Multiply the numerators and multiply the denominators. Would you like a worked example?" },
            },
            latest = "Yes, please show me the worked example you just offered.",
        });

        Assert.True(await client.ShouldReply(input, autonomous: false, minProbability: .95, default),
            "Expected the production client to approve an explicit continuation within its three-second timeout");
    }

    [SkippableFact]
    public async Task DecisionsDeploymentReturnsNoulProbabilities()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var settings = AzureDecisionsSettings.FromEnvironment();
        Skip.IfNot(settings.IsConfigured, "Azure Decisions deployment is not configured");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 32768,
        };
        using var request = settings.CreateRequest();
        request.Content = JsonContent.Create(new
        {
            model = settings.Deployment,
            state = "The circle is blue.",
            questions = new
            {
                mentions_blue = new { type = "noul", instructions = "Does the input mention blue?" },
                mentions_red = new { type = "noul", instructions = "Does the input mention red?" },
            },
        });
        await request.Content.LoadIntoBufferAsync(128 * 1024);

        using var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"Azure Decisions failed: HTTP {(int)response.StatusCode}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var answers = body.RootElement.GetProperty("answers");
        var blue = answers.GetProperty("mentions_blue").GetProperty("noul").GetDouble();
        var red = answers.GetProperty("mentions_red").GetProperty("noul").GetDouble();
        Assert.InRange(blue, 0, 1);
        Assert.InRange(red, 0, 1);
        Assert.True(blue > .5, "Expected the explicitly mentioned color to be recognized");
        Assert.True(red < .5, "Expected an absent color not to be recognized");
    }

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

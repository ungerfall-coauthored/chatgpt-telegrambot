using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OpenAI.Interfaces;
using OpenAI.Managers;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels.ImageResponseModel;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using WebhookFunction = Ungerfall.ChatGpt.TelegramBot.AzureFunction.TelegramBot;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class AzureOpenAiTests
{
    [Fact]
    public void WebhookRejectsMissingWrongAndMultipleSecrets()
    {
        Assert.False(WebhookFunction.ValidateSecret(null, ["secret"]));
        Assert.False(WebhookFunction.ValidateSecret("secret", []));
        Assert.False(WebhookFunction.ValidateSecret("secret", ["wrong"]));
        Assert.False(WebhookFunction.ValidateSecret("secret", ["secret", "secret"]));
        Assert.True(WebhookFunction.ValidateSecret("secret", ["secret"]));
    }

    [Fact]
    public async Task WebhookQueuesOnlyAuthenticatedUpdates()
    {
        var previous = Environment.GetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET", "test-secret");
        try
        {
            var context = Substitute.For<FunctionContext>();
            var request = Substitute.For<HttpRequestData>(context);
            var response = Substitute.For<HttpResponseData>(context);
            var headers = new HttpHeadersCollection();
            request.Headers.Returns(headers);
            request.Body.Returns(new MemoryStream(Encoding.UTF8.GetBytes("""{"update_id":42}""")));
            request.CreateResponse().Returns(response);
            response.Headers.Returns(new HttpHeadersCollection());
            var function = new WebhookFunction(NullLoggerFactory.Instance);

            var rejected = await function.Run(request);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.HttpResponseData.StatusCode);
            Assert.Null(rejected.Update);

            headers.Add("X-Telegram-Bot-Api-Secret-Token", "test-secret");
            var accepted = await function.Run(request);
            Assert.Equal(HttpStatusCode.OK, accepted.HttpResponseData.StatusCode);
            Assert.Equal(42, accepted.Update?.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET", previous);
        }
    }

    [Theory]
    [InlineData("http://example.openai.azure.com/")]
    [InlineData("https://example.openai.azure.com/openai")]
    [InlineData("https://user:password@example.openai.azure.com/")]
    public void SettingsRejectInvalidEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new AzureOpenAiSettings(endpoint, "key", "chat", null, null, "2024-10-21"));
    }

    [Fact]
    public async Task ClientsUseAzureDeploymentsAndApiKey()
    {
        var settings = TestAzureSettings.Create();
        foreach (var (deployment, image) in new[] { (settings.ChatDeployment, false), (settings.SummaryDeployment, false), (settings.ImageDeployment!, true) })
        {
            var handler = new CaptureHandler(image);
            using var http = new HttpClient(handler);
            using var service = new OpenAIService(settings.CreateOptions(deployment, image), http);
            if (image)
            {
                var result = await service.Image.CreateImage(new ImageCreateRequest { Prompt = "test", Size = "1024x1024", N = 1 });
                Assert.Equal("aGVsbG8=", Assert.Single(result.Results).B64);
            }
            else
            {
                var result = await service.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
                {
                    Messages = [ChatMessage.FromUser("test")],
                    Model = deployment
                }, deployment);
                Assert.Equal("ok", result.Choices[0].Message.Content);
                using var body = JsonDocument.Parse(handler.Body!);
                Assert.Equal(deployment, body.RootElement.GetProperty("model").GetString());
            }

            var operation = image ? "images/generations" : "chat/completions";
            var version = image ? "2025-04-01-preview" : "2024-10-21";
            Assert.Equal($"https://example.openai.azure.com/openai/deployments/{deployment}/{operation}?api-version={version}", handler.Uri);
            Assert.Equal("test-key", handler.ApiKey);
            Assert.Null(handler.Authorization);
        }
    }

    [Fact]
    public async Task ImageCommandUploadsAzureBase64Result()
    {
        var client = Substitute.For<IOpenAIService>();
        client.Image.CreateImage(Arg.Any<ImageCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ImageCreateResponse { Results = [new ImageCreateResponse.ImageDataResult { B64 = "aGVsbG8=" }] });
        var bot = Substitute.For<ITelegramBotClient>();
        bot.SendRequest<Message>(Arg.Any<SendPhotoRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());
        var command = new GenerateImage(new ImageOpenAiClient(client), NullLogger<GenerateImage>.Instance, bot);

        await command.Execute(new Message { Id = 1, Chat = new Chat { Id = 123 } }, "draw a circle", default);

        await bot.Received(1).SendRequest<Message>(Arg.Any<SendPhotoRequest>(), Arg.Any<CancellationToken>());
        await client.Image.Received(1).CreateImage(
            Arg.Is<ImageCreateRequest>(r => r.Size == "1024x1024" && r.N == 1), Arg.Any<CancellationToken>());
    }

    private sealed class CaptureHandler(bool image) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.ToString();
            ApiKey = request.Headers.GetValues("api-key").Single();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = image
                ? """{"created":1,"data":[{"b64_json":"aGVsbG8="}]}"""
                : """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}

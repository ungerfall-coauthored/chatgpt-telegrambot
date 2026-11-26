using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
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
        Assert.Throws<ArgumentException>(() =>
            new AzureOpenAiSettings(endpoint, "key", "chat", null, null, "2025-04-01-preview"));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(null)]
    public async Task ChatClientUsesAzureV1DeploymentAndRequestSettings(float? temperature)
    {
        var profile = new CompletionSettings(temperature: temperature, maxCompletionTokens: 4096);
        var settings = TestAzureSettings.Create(chat: profile);
        var handler = new CaptureHandler(image: false);
        using var http = new HttpClient(handler);
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http);

        var result = await client.CompleteAsync(profile.CreateRequest(
            [ChatMessage.FromUser("test")], settings.ChatDeployment, user: "123"));

        Assert.True(result.Successful);
        Assert.Equal("ok", result.Content);
        Assert.Equal("https://example.openai.azure.com/openai/v1/chat/completions", handler.Uri);
        Assert.Equal("Bearer test-key", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(settings.ChatDeployment, body.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("123", body.RootElement.GetProperty("user").GetString());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        if (temperature == null)
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        else
            Assert.Equal(temperature.Value, body.RootElement.GetProperty("temperature").GetSingle());
    }

    [Fact]
    public async Task ImageClientPreservesAzureDeploymentEndpointAndRequestShape()
    {
        var settings = TestAzureSettings.Create();
        var handler = new CaptureHandler(image: true);
        using var http = new HttpClient(handler);
        using var client = new ImageOpenAiClient(settings.CreateImageGenerationClient(http, settings.ImageDeployment!));

        var result = await client.Client!.GenerateAsync("test");

        Assert.True(result.Successful);
        Assert.Equal("aGVsbG8=", result.Base64);
        Assert.Equal(
            "https://example.openai.azure.com/openai/deployments/test-image/images/generations?api-version=2025-04-01-preview",
            handler.Uri);
        Assert.Equal("test-key", handler.ApiKey);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("test", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("1024x1024", body.RootElement.GetProperty("size").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task ImageCommandUploadsAzureBase64Result()
    {
        var client = Substitute.For<IImageGenerationClient>();
        client.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ImageGenerationResult(true, "aGVsbG8=", null));
        var bot = Substitute.For<ITelegramBotClient>();
        bot.SendRequest<Message>(Arg.Any<SendPhotoRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());
        var command = new GenerateImage(new ImageOpenAiClient(client), NullLogger<GenerateImage>.Instance, bot);

        await command.Execute(new Message { Id = 1, Chat = new Chat { Id = 123 } }, "draw a circle", default);

        await bot.Received(1).SendRequest<Message>(Arg.Any<SendPhotoRequest>(), Arg.Any<CancellationToken>());
        await client.Received(1).GenerateAsync("draw a circle", Arg.Any<CancellationToken>());
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
            ApiKey = request.Headers.TryGetValues("api-key", out var apiKeys) ? apiKeys.Single() : null;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = image
                ? """{"created":1,"data":[{"b64_json":"aGVsbG8="}]}"""
                : """{"id":"test","object":"chat.completion","created":1,"model":"test-chat","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}

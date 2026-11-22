using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Conversation policy environment")]
public class DockingGameLiveTests
{
    [SkippableFact]
    public async Task GameSendAndCallbackUseTelegramApi()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DOCKING_LIVE_TEST") == "1", "Docking live test is not enabled");
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required");

        var chatId = long.Parse(Required("DOCKING_TEST_CHAT"));
        var oldUsers = Environment.GetEnvironmentVariable("TestUsers");
        var oldUsername = Environment.GetEnvironmentVariable("TELEGRAM_BOT_USERNAME");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var transport = new CaptureGameRequests();
        using var http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(100) };
        var bot = new TelegramBotClient(Required("IntegrationTests_TELEGRAM_BOT_TOKEN"), http);
        var me = await bot.GetMe(timeout.Token);
        Assert.Equal("chatgpt_ungerfall_testing_bot", me.Username);
        Assert.Equal(ChatType.Private, (await bot.GetChat(chatId, timeout.Token)).Type);
        Environment.SetEnvironmentVariable("TestUsers", chatId.ToString());
        Environment.SetEnvironmentVariable("TELEGRAM_BOT_USERNAME", me.Username);
        int? anchorId = null;
        try
        {
            using var scenario = new NaturalRequestScenario(new DecisionsClient(
                http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()), bot: bot, chatId: chatId);
            var anchor = await bot.SendMessage(chatId, "Автоматический тест игры.", cancellationToken: timeout.Token);
            anchorId = anchor.Id;
            await scenario.Handler.Handle(new Update
            {
                Message = new Message
                {
                    Id = anchor.Id, Chat = new Chat { Id = chatId, Type = ChatType.Private },
                    From = new User { Id = chatId, FirstName = "Test" }, Text = "/docking",
                },
            }, timeout.Token);
            Assert.NotNull(transport.GameMessageId);
            Assert.Equal("docking", transport.GameShortName);

            await scenario.Handler.Handle(new Update
            {
                CallbackQuery = new CallbackQuery
                {
                    Id = "expired-test-callback", GameShortName = "docking",
                    From = new User { Id = chatId, FirstName = "Test" },
                    Message = new Message { Chat = new Chat { Id = chatId, Type = ChatType.Private } },
                },
            }, timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, transport.CallbackStatus);
            Assert.Equal(Commands.DockingGame.GameUrl, transport.CallbackUrl);
        }
        finally
        {
            if (transport.GameMessageId is { } gameId)
                await bot.DeleteMessage(chatId, gameId, cancellationToken: CancellationToken.None);
            if (anchorId is { } id)
                await bot.DeleteMessage(chatId, id, cancellationToken: CancellationToken.None);
            Environment.SetEnvironmentVariable("TestUsers", oldUsers);
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_USERNAME", oldUsername);
        }
    }

    private sealed class CaptureGameRequests() : DelegatingHandler(new HttpClientHandler())
    {
        public int? GameMessageId { get; private set; }
        public string? GameShortName { get; private set; }
        public string? CallbackUrl { get; private set; }
        public HttpStatusCode? CallbackStatus { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            bool game = path.EndsWith("/sendGame", StringComparison.OrdinalIgnoreCase);
            bool callback = path.EndsWith("/answerCallbackQuery", StringComparison.OrdinalIgnoreCase);
            if (game || callback)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (game) GameShortName = body.RootElement.GetProperty("game_short_name").GetString();
                if (callback) CallbackUrl = body.RootElement.GetProperty("url").GetString();
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (game && response.IsSuccessStatusCode)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                GameMessageId = body.RootElement.GetProperty("result").GetProperty("message_id").GetInt32();
                Assert.True(body.RootElement.GetProperty("result").TryGetProperty("game", out _));
            }
            if (callback) CallbackStatus = response.StatusCode;
            return response;
        }
    }
}

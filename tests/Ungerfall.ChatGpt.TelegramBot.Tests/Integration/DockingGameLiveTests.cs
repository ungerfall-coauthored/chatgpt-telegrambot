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
    public async Task PrivateCommandAndGameCallbackReachTelegram()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DOCKING_LIVE_TEST") == "1", "Docking live test is not enabled");
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required");

        var chatId = long.Parse(Required("DOCKING_TEST_CHAT"));
        var shortName = Required("DOCKING_GAME_SHORT_NAME");
        var oldUsers = Environment.GetEnvironmentVariable("TestUsers");
        var oldUsername = Environment.GetEnvironmentVariable("TELEGRAM_BOT_USERNAME");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var bot = new TelegramBotClient(Required("IntegrationTests_TELEGRAM_BOT_TOKEN"), http);
        var me = await bot.GetMe(timeout.Token);
        Assert.Equal("chatgpt_ungerfall_testing_bot", me.Username);
        Assert.True(string.IsNullOrEmpty((await bot.GetWebhookInfo(timeout.Token)).Url), "Test bot must not have a webhook.");
        Assert.Equal(ChatType.Private, (await bot.GetChat(chatId, timeout.Token)).Type);
        Environment.SetEnvironmentVariable("TestUsers", chatId.ToString());
        Environment.SetEnvironmentVariable("TELEGRAM_BOT_USERNAME", me.Username);
        try
        {
            using var scenario = new NaturalRequestScenario(new DecisionsClient(
                http, NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings()), bot: bot, chatId: chatId);
            var start = DateTime.UtcNow;
            await bot.SendMessage(chatId, "Тест игры: отправьте /docking, затем нажмите Play.", cancellationToken: timeout.Token);
            int? offset = null;
            int? commandId = null;
            while (true)
            {
                var updates = await bot.GetUpdates(offset: offset, timeout: 5, cancellationToken: timeout.Token);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    if (commandId == null && update.Message is { } message
                        && message.Chat.Id == chatId && message.From is { IsBot: false }
                        && message.Date >= start.AddSeconds(-1)
                        && (message.Text == "/docking" || message.Text == $"/docking@{me.Username}"))
                    {
                        await scenario.Handler.Handle(update, timeout.Token);
                        commandId = message.Id;
                    }
                    else if (commandId != null && update.CallbackQuery is { } query
                        && query.From.Id == chatId && query.GameShortName == shortName
                        && query.Message?.Chat.Id == chatId && query.Message.ReplyToMessage?.Id == commandId)
                    {
                        await scenario.Handler.Handle(update, timeout.Token);
                        return;
                    }
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TestUsers", oldUsers);
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_USERNAME", oldUsername);
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
public class ReleaseAnnouncementLiveTests
{
    private const string Version = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [SkippableFact]
    public async Task SendsAnnouncementThroughTelegramClientToPrivateTestChat()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RELEASE_ANNOUNCEMENT_LIVE_TEST") == "1",
            "Release announcement live test is not enabled");
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required");

        var chatId = long.Parse(Required("BOOK_QUIZ_TEST_CHAT"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var bot = new TelegramBotClient(Required("IntegrationTests_TELEGRAM_BOT_TOKEN"), http);
        Assert.Equal("chatgpt_ungerfall_testing_bot", (await bot.GetMe(timeout.Token)).Username);
        Assert.Equal(ChatType.Private, (await bot.GetChat(chatId, timeout.Token)).Type);

        var repository = Substitute.For<IReleaseAnnouncementRepository>();
        var receipt = new ReleaseAnnouncementReceipt { ChatId = chatId, Version = Version };
        repository.Begin(chatId, Version, Arg.Any<CancellationToken>()).Returns(receipt);
        var notes = Substitute.For<IReleaseNotesSource>();
        notes.GetTitles(null, Version, Arg.Any<CancellationToken>()).Returns([]);
        var service = new ReleaseAnnouncementService(
            new ReleaseAnnouncementSettings(enabled: true, version: Version, requireDeploymentApproval: false),
            chatId, repository, notes, new SummaryOpenAiClient(Substitute.For<IChatCompletionClient>(), "summary"),
            bot, NullLogger<ReleaseAnnouncementService>.Instance);

        try
        {
            Assert.True(await service.Execute(timeout.Token));
            Assert.Equal(ReleaseAnnouncementReceipt.Completed, receipt.Stage);
            Assert.NotNull(receipt.TelegramMessageId);
        }
        finally
        {
            if (receipt.TelegramMessageId is { } messageId)
                await bot.DeleteMessage(chatId, messageId, cancellationToken: CancellationToken.None);
        }
    }
}

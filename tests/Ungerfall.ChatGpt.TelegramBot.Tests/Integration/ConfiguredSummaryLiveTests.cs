using System.Collections.Immutable;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.AzureFunction;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Conversation policy environment")]
public class ConfiguredSummaryLiveTests
{
    [SkippableFact]
    public async Task ConfiguredDailyAndTldrPromptsUseActualSummaryClient()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var azure = AzureOpenAiSettings.FromEnvironment();
        using var summary = new SummaryOpenAiClient(azure.CreateChatCompletionClient(azure.SummaryDeployment),
            azure.SummaryDeployment, azure.Summary);
        var history = Substitute.For<ITelegramMessageRepository>();
        history.Get(-1, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(
            new[]
            {
                new TelegramMessage { User = "Friend", Message = "Мы договорились встретиться завтра.", Date = DateTime.UtcNow, MessageId = 1 },
                new TelegramMessage { User = "Friend", Message = "[activity] sent an image", Date = DateTime.UtcNow.AddSeconds(-2), MessageId = 2 },
                new TelegramMessage { User = "Friend", Message = "[activity] sent an image", Date = DateTime.UtcNow.AddSeconds(-1), MessageId = 3 },
                new TelegramMessage { User = "Friend", Message = "[activity] requested image generation: a red circle", Date = DateTime.UtcNow.AddSeconds(1), MessageId = 4 },
            }.ToAsyncEnumerable());
        var whitelist = Substitute.For<IWhitelist>();
        whitelist.GetSummarySystemRoleMessage(-1).Returns(PromptCatalog.Load().Summaries.DefaultSystem);
        var bot = Substitute.For<ITelegramBotClient>();
        bot.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 1 });
        var cache = Substitute.For<ITimedTaskExecutionRepository>();
        cache.BeginTldr(-1, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TldrCache());
        var chats = Substitute.For<DailySummaryChats>();
        chats.Get().Returns(ImmutableHashSet.Create(-1L));

        await new DailySummary(chats, bot, cache, NullLogger<DailySummary>.Instance, whitelist, history, summary).Execute();
        await new TooLongDidNotReadToday(history, new TokenCounter(), summary,
            NullLogger<TooLongDidNotReadToday>.Instance, bot, whitelist, cache, azure).Execute(-1);

        await cache.Received(1).Create(Arg.Any<TimedTaskExecution>(), Arg.Any<CancellationToken>());
        await cache.Received(1).CompleteTldr(Arg.Any<TldrCache>(), Arg.Any<CancellationToken>());
        await bot.Received(2).SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [SkippableFact]
    public async Task ConfiguredShrinkPromptUsesActualChatClient()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var azure = AzureOpenAiSettings.FromEnvironment();
        using var service = new SummaryOpenAiClient(
            azure.CreateChatCompletionClient(azure.ChatDeployment), azure.ChatDeployment, azure.Chat);
        var history = Substitute.For<ITelegramMessageRepository>();
        var message = new TelegramMessage
        {
            Id = "synthetic",
            Message = string.Join(" ", Enumerable.Repeat("Завтра мы обсудим план мероприятия.", 50)),
            DateUtc = DateTime.UtcNow.AddDays(-1).ToString(TelegramMessage.DATE_UTC_FORMAT),
            User = "Friend",
        };
        history.GetOldMessages(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message }.ToAsyncEnumerable());
        history.TryBeginShrink(message, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var counter = Substitute.For<ITokenCounter>();
        counter.Count(message.Message).Returns(1024);
        counter.Count(Arg.Is<string>(text => text != message.Message)).Returns(10);

        await new ShrinkMessage(service.Client, counter, NullLogger<ShrinkMessage>.Instance, history, azure)
            .Run(new TimerInfo { ScheduleStatus = default, IsPastDue = default });

        await history.Received(1).Update(Arg.Is<TelegramMessage>(m => m.IsShrunk && !string.IsNullOrWhiteSpace(m.Message)),
            Arg.Any<CancellationToken>());
    }
}

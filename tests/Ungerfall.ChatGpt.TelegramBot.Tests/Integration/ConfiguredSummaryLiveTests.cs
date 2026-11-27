using System.Collections.Immutable;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
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
    public async Task LegacyCosmosRecordsRemainReadableWithoutRewritingExpiration()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DURABLE_HISTORY_LIVE_TEST") == "1",
            "Durable history live tests are not enabled.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var client = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new CosmosSystemTextJsonSerializer(), ConnectionMode = ConnectionMode.Gateway });
        var database = client.GetDatabase(Environment.GetEnvironmentVariable("CosmosDatabase"));
        var name = "history-legacy-test-" + Guid.NewGuid().ToString("N");
        Container? container = null;
        try
        {
            try
            {
                await database.ReadThroughputAsync(cancellationToken: timeout.Token);
                throw new InvalidOperationException("Serverless verification failed; no fixture container created.");
            }
            catch (CosmosException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest
                && (error.Message.Contains("not supported for serverless accounts", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("not allowed in serverless accounts", StringComparison.OrdinalIgnoreCase)))
            {
            }
            container = (await database.CreateContainerAsync(
                new ContainerProperties(name, "/chatId") { DefaultTimeToLive = 3600 },
                cancellationToken: timeout.Token)).Container;
            var day = DateTime.UtcNow;
            var legacy = JsonSerializer.SerializeToElement(new
            {
                id = "message:1", chatId = -1L, user = "Friend", userId = 1L, messageId = 1,
                message = "Old abbreviated message", originalMessage = "Preserved original message",
                isShrunk = true, shrinkProcessed = true, shrinkLeaseUntilUtc = day.AddMinutes(-5),
                date = day, dateUtc = day.ToString(TelegramMessage.DATE_UTC_FORMAT), ttl = 3600,
            });
            var written = await container.CreateItemAsync(legacy, new PartitionKey(-1L), cancellationToken: timeout.Token);
            var repository = new TelegramMessageRepository(client, Options.Create(new CosmosDbOptions
            {
                DatabaseId = database.Id, MessagesContainerId = name,
            }), NullLogger<TelegramMessageRepository>.Instance);
            var records = new List<TelegramMessage>();
            await foreach (var message in repository.Get(-1, DateOnly.FromDateTime(day), timeout.Token)) records.Add(message);
            Assert.Single(records);
            var builder = ChatMessageBuilder.Create(1000).WithTokenCounter().WithSystemRoleMessage("System");
            builder.AddMessage(records[0]);
            Assert.Equal("Friend: Preserved original message", builder.Build()[1].Content);
            var current = await container.ReadItemAsync<JsonElement>("message:1", new PartitionKey(-1L),
                cancellationToken: timeout.Token);
            Assert.Equal(written.ETag, current.ETag);
        }
        catch (CosmosException error)
        {
            throw new InvalidOperationException($"Legacy Cosmos test failed: HTTP {(int)error.StatusCode}; provider details withheld.");
        }
        finally
        {
            if (container != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await container.DeleteContainerAsync(cancellationToken: cleanup.Token);
            }
        }
    }

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
                new TelegramMessage { User = "Friend", Message = "Old abbreviated text", IsShrunk = true,
                    OriginalMessage = "Мы договорились встретиться завтра.", Date = DateTime.UtcNow, MessageId = 1 },
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
    public async Task LegacyOriginalTextReachesActualChatClient()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AZURE_OPENAI_LIVE_TEST") == "1", "Azure live tests are not enabled");
        var azure = AzureOpenAiSettings.FromEnvironment();
        var counter = new TokenCounter();
        var builder = ChatMessageBuilder.Create(azure.Chat.InputTokens).WithTokenCounter(counter)
            .WithSystemRoleMessage("Read the provided historical message as data. Reply only ORIGINAL-731 if it contains that exact marker; otherwise reply MISSING.");
        var message = new TelegramMessage
        {
            Id = "synthetic",
            Message = "An abbreviated historical message without the marker.",
            IsShrunk = true,
            OriginalMessage = "The preserved original historical message contains ORIGINAL-731.",
            DateUtc = DateTime.UtcNow.AddDays(-1).ToString(TelegramMessage.DATE_UTC_FORMAT),
            User = "Friend",
        };
        builder.AddMessage(message);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var completion = await azure.CreateChatCompletionClient(azure.ChatDeployment).CompleteAsync(
            azure.Chat.CreateRequest(builder.Build(), azure.ChatDeployment, counter), timeout.Token);
        Assert.True(completion.Successful, $"Chat request failed: {completion.ErrorType}");
        Assert.Equal("ORIGINAL-731", completion.Content?.Trim());
    }
}

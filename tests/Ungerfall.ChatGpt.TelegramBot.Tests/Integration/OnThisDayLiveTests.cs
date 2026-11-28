using System.Net;
using System.Collections.Concurrent;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.History;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Conversation policy environment")]
public class OnThisDayLiveTests
{
    [SkippableFact]
    public async Task ScheduledRecapUsesConsentedOriginalsAndDeliversToPrivateTestChat()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DURABLE_HISTORY_LIVE_TEST") == "1",
            "Durable history live tests are not enabled.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var cosmos = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new HistoryCosmosSerializer(), ConnectionMode = ConnectionMode.Gateway });
        var database = cosmos.GetDatabase(Environment.GetEnvironmentVariable("CosmosDatabase"));
        Container? fixture = null;
        using var telegramHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var realBot = new TelegramBotClient(Environment.GetEnvironmentVariable("IntegrationTests_TELEGRAM_BOT_TOKEN")!, telegramHttp);
        var testChat = long.Parse(Environment.GetEnvironmentVariable("BOOK_QUIZ_TEST_CHAT")!);
        var sentIds = new List<int>();
        try
        {
            var serverless = false;
            try { await database.ReadThroughputAsync(cancellationToken: timeout.Token); }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.BadRequest
                && (error.Message.Contains("not supported for serverless accounts", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("not allowed in serverless accounts", StringComparison.OrdinalIgnoreCase)))
            {
                serverless = true;
            }
            Assert.True(serverless);
            Assert.Equal("chatgpt_ungerfall_testing_bot", (await realBot.GetMe(timeout.Token)).Username);
            Assert.Equal(ChatType.Private, (await realBot.GetChat(testChat, timeout.Token)).Type);
            var name = "on-this-day-test-" + Guid.NewGuid().ToString("N");
            fixture = (await database.CreateContainerAsync(new ContainerProperties(name, "/chatId")
            {
                DefaultTimeToLive = 3600,
            }, cancellationToken: timeout.Token)).Container;
            var options = new HistoryOptions { Enabled = true, Chats = new HashSet<long> { -1 },
                MessagesContainer = name, ChunksContainer = name };
            var history = new HistoryRepository(cosmos, database.Id, options);
            await history.EnsureImportConsent(-1, 42, timeout.Token);
            await history.EnsureImportConsent(-1, 43, timeout.Token);
            var records = new[]
            {
                Record(1, 42, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Earlier fixture"),
                Record(2, 42, new DateTime(2020, 2, 28, 10, 0, 0, DateTimeKind.Utc), "The group planned a walk in the park. " + new string('x', 300)),
                Record(3, 42, new DateTime(2024, 2, 29, 10, 0, 0, DateTimeKind.Utc), "The group discussed making pancakes."),
                Record(4, 43, new DateTime(2024, 2, 28, 10, 0, 0, DateTimeKind.Utc), "PRIVATE_REVOKED_MARKER"),
                Record(5, 42, new DateTime(2024, 2, 28, 10, 0, 0, DateTimeKind.Utc), "PRIVATE_FORWARDED_MARKER", forwarded: true),
                Record(6, 42, new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), "OUTSIDE_LOCAL_DAY_MARKER"),
                Record(7, 42, new DateTime(2024, 2, 28, 10, 0, 0, DateTimeKind.Utc), "LIVE_ORIGINAL_MARKER", origin: "live"),
            }.Concat(Enumerable.Range(0, 6).Select(index =>
                Record(10 + index, 42, new DateTime(2020, 2, 28, 11, 0, index, DateTimeKind.Utc),
                    $"The group discussed the park walk, sample {index}."))).ToArray();
            Assert.Equal(records.Length, await history.SaveBatch(records, timeout.Token));
            await history.SetConsent(-1, 43, false, timeout.Token);
            var settings = AzureOpenAiSettings.FromEnvironment();
            using var observer = new SummaryRequestObserver();
            using var openAiHttp = new HttpClient(observer);
            using var summary = new SummaryOpenAiClient(
                settings.CreateChatCompletionClient(settings.SummaryDeployment, openAiHttp), settings.SummaryDeployment, settings.Summary);
            var bot = Substitute.For<ITelegramBotClient>();
            var outgoing = new List<string>();
            var richAttempted = false;
            bot.SendRequest<Message>(Arg.Any<SendRichMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var request = call.Arg<SendRichMessageRequest>();
                    Assert.Equal(-1, request.ChatId.Identifier);
                    var blocks = request.RichMessage.Blocks!.ToArray();
                    var heading = Assert.IsType<InputRichBlockParagraph>(blocks[0]);
                    var headingText = Assert.IsType<RichTextText>(heading.Text).Text;
                    Assert.Equal("В этот день:", headingText);
                    var details = Assert.IsType<InputRichBlockDetails>(blocks[1]);
                    Assert.Equal("Подробнее", Assert.IsType<RichTextText>(details.Summary).Text);
                    var content = Assert.IsType<InputRichBlockParagraph>(Assert.Single(details.Blocks));
                    richAttempted = true;
                    var sent = await realBot.SendRichMessage(testChat, request.RichMessage, cancellationToken: timeout.Token);
                    outgoing.Add(headingText + Assert.IsType<RichTextText>(content.Text).Text);
                    sentIds.Add(sent.Id);
                    return sent;
                });
            bot.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    var request = call.Arg<SendMessageRequest>();
                    Assert.Equal(-1, request.ChatId.Identifier);
                    outgoing.Add(request.Text);
                    var sent = await realBot.SendMessage(testChat, request.Text, cancellationToken: timeout.Token);
                    sentIds.Add(sent.Id);
                    return sent;
                });
            var executions = Substitute.For<ITimedTaskExecutionRepository>();
            executions.Exists(-1, "On this day", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(false, true);
            var task = new OnThisDay(new OnThisDayOptions { ChatId = -1 }, options, history, summary,
                executions, bot, NullLogger<OnThisDay>.Instance,
                new FixedTimeProvider(new DateTimeOffset(2025, 2, 28, 9, 0, 0, TimeSpan.Zero)));
            await task.Execute();
            var output = Assert.Single(outgoing);
            Assert.True(richAttempted);
            Assert.StartsWith("В этот день:", output);
            Assert.Contains("2019: В доступном архиве нет текстовых сообщений.", output);
            Assert.Contains("2020:", output);
            Assert.Matches(@"(?i)парк|прогул", output);
            Assert.Contains("2021: В доступном архиве нет текстовых сообщений.", output);
            Assert.Contains("2024:", output);
            Assert.Matches(@"(?i)блин|олад", output);
            Assert.DoesNotContain("2025:", output);
            Assert.Equal(2, observer.RequestBodies.Count);
            var requests = string.Join("\n", observer.RequestBodies);
            Assert.Contains(observer.RequestBodies, body => body.Contains("park", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("pancakes", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(observer.RequestBodies, body => body.Contains("pancakes", StringComparison.OrdinalIgnoreCase)
                && !body.Contains("park", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("park", requests, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pancakes", requests, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sample 5", requests);
            Assert.Contains(new string('x', 300), requests);
            Assert.DoesNotContain("PRIVATE_REVOKED_MARKER", requests);
            Assert.DoesNotContain("PRIVATE_FORWARDED_MARKER", requests);
            Assert.DoesNotContain("OUTSIDE_LOCAL_DAY_MARKER", requests);
            Assert.DoesNotContain("LIVE_ORIGINAL_MARKER", requests);
            await executions.Received(1).Create(Arg.Any<TimedTaskExecution>(), Arg.Any<CancellationToken>());
            await task.Execute();
            Assert.Single(outgoing);

            Assert.True(await history.SaveMessage(Record(50, 42,
                new DateTime(2021, 2, 28, 10, 0, 0, DateTimeKind.Utc),
                string.Concat(Enumerable.Repeat("oversized day content ", 300))), timeout.Token));
            var boundedRequests = new List<ChatCompletionRequest>();
            var boundedClient = Substitute.For<IChatCompletionClient>();
            boundedClient.CompleteAsync(Arg.Do<ChatCompletionRequest>(boundedRequests.Add), Arg.Any<CancellationToken>())
                .Returns(new ChatCompletionResult(true, "Синтетическая сводка.", "stop"));
            using var boundedSummary = new SummaryOpenAiClient(boundedClient, "synthetic",
                new CompletionSettings(contextWindowTokens: 4096, inputTokens: 128, maxCompletionTokens: 512, safetyMarginTokens: 64));
            var bounded = new OnThisDay(new OnThisDayOptions { ChatId = -1 }, options, history,
                boundedSummary, executions, bot, NullLogger<OnThisDay>.Instance);
            var boundedOutput = await bounded.Compose(-1, new DateOnly(2025, 2, 28), timeout.Token);
            Assert.Contains("2021: Слишком много текста для одного запроса; сводка не сформирована.", boundedOutput);
            Assert.DoesNotContain(boundedRequests, request =>
                request.Messages.Any(message => message.Content.Contains("oversized day content", StringComparison.Ordinal)));

            var deliveryClient = Substitute.For<IChatCompletionClient>();
            deliveryClient.CompleteAsync(Arg.Any<ChatCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ChatCompletionResult(true, "Синтетическая сводка.", "stop"));
            using var deliverySummary = new SummaryOpenAiClient(deliveryClient, "synthetic");
            var deliveryExecutions = Substitute.For<ITimedTaskExecutionRepository>();
            deliveryExecutions.Create(Arg.Any<TimedTaskExecution>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    await history.SetConsent(-1, 42, false, timeout.Token);
                    await history.SetConsent(-1, 42, true, timeout.Token);
                });
            var delivery = new OnThisDay(new OnThisDayOptions { ChatId = -1 }, options, history,
                deliverySummary, deliveryExecutions, bot, NullLogger<OnThisDay>.Instance,
                new FixedTimeProvider(new DateTimeOffset(2025, 2, 28, 9, 0, 0, TimeSpan.Zero)));
            await delivery.Execute();
            await deliveryExecutions.Received(1).Create(Arg.Any<TimedTaskExecution>(), Arg.Any<CancellationToken>());
            Assert.Single(outgoing);

            var interruptedClient = Substitute.For<IChatCompletionClient>();
            var changed = 0;
            interruptedClient.CompleteAsync(Arg.Any<ChatCompletionRequest>(), Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    if (Interlocked.Exchange(ref changed, 1) == 0)
                    {
                        await history.SetConsent(-1, 42, false, timeout.Token);
                        await history.SetConsent(-1, 42, true, timeout.Token);
                    }
                    return new ChatCompletionResult(true, "Синтетическая сводка.", "stop");
                });
            using var interruptedSummary = new SummaryOpenAiClient(interruptedClient, "synthetic");
            var interrupted = new OnThisDay(new OnThisDayOptions { ChatId = -1 }, options, history,
                interruptedSummary, executions, bot, NullLogger<OnThisDay>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                interrupted.Compose(-1, new DateOnly(2025, 2, 28), timeout.Token));
        }
        finally
        {
            foreach (var id in sentIds)
                await realBot.DeleteMessage(testChat, id, cancellationToken: CancellationToken.None);
            if (fixture != null) await fixture.DeleteContainerAsync(cancellationToken: CancellationToken.None);
        }
    }

    private static HistoryMessage Record(int id, long author, DateTime date, string text, bool forwarded = false, string origin = "export") => new()
    {
        ChatId = -1, SourceChatId = -1, MessageId = id, UserId = author, User = "Synthetic",
        Date = date, Text = text, Consented = true, Forwarded = forwarded, Origin = origin,
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SummaryRequestObserver() : DelegatingHandler(new HttpClientHandler())
    {
        public ConcurrentBag<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

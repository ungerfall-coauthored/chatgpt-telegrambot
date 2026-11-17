using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.Interfaces;
using OpenAI.ObjectModels.ResponseModels;
using OpenAI.ObjectModels.SharedModels;
using Telegram.Bot;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class CompletionSettingsTests
{
    [Theory]
    [InlineData(128000, 127000, 2048, 1024)]
    [InlineData(int.MaxValue, int.MaxValue, 1, 0)]
    [InlineData(128000, 0, 2048, 1024)]
    [InlineData(128000, 16384, 0, 1024)]
    [InlineData(128000, 16384, 2048, -1)]
    public void RejectsInvalidBudgets(int window, int input, int output, int margin)
    {
        Assert.Throws<ArgumentException>(() => new CompletionSettings(window, input, output, margin));
    }

    [Theory]
    [InlineData("200000", "not-a-number", 200000)]
    [InlineData(null, "65536", 65536)]
    public void EnvironmentAllowsLargeBudgetsAndHonorsLegacyFallback(string? input, string legacy, int expected)
    {
        var variables = new Dictionary<string, string?>
        {
            ["SummaryChunkTokens"] = legacy,
            ["AZURE_OPENAI_SUMMARY_CONTEXT_WINDOW_TOKENS"] = "400000",
            ["AZURE_OPENAI_SUMMARY_INPUT_TOKENS"] = input,
            ["AZURE_OPENAI_SUMMARY_MAX_COMPLETION_TOKENS"] = "8192",
            ["AZURE_OPENAI_SUMMARY_SAFETY_MARGIN_TOKENS"] = "2048",
            ["AZURE_OPENAI_SUMMARY_TEMPERATURE"] = "omit",
        };
        var settings = CompletionSettings.FromConfiguration(name => variables.GetValueOrDefault(name), summary: true);
        var request = settings.CreateRequest([ChatMessage.FromUser("hello")], "mini");
        Assert.Equal(expected, settings.InputTokens);
        Assert.Equal(8192, request.MaxCompletionTokens);
        Assert.Null(request.Temperature);
    }

    [Fact]
    public async Task HistoryUsesConfiguredBudgetAndPreservesCurrentMessageAndOrdering()
    {
        var repository = Substitute.For<ITelegramMessageRepository>();
        var counter = Substitute.For<ITokenCounter>();
        counter.Count(Arg.Any<string>()).Returns(x => ((string)x[0]).Length);
        var whitelist = Substitute.For<IWhitelist>();
        whitelist.GetSystemRoleMessage(1).Returns("system");
        var messages = Enumerable.Range(1, 6).Reverse().Select(i => new TelegramMessage
        {
            User = "friend", Message = i + new string('x', 1000), MessageId = i,
        }).ToArray();
        repository.GetAllOrderByDateDescending(1, Arg.Any<CancellationToken>()).Returns(messages.ToAsyncEnumerable());

        var small = new ConversationHistory(repository, "latest", counter, whitelist, inputTokens: 2100);
        var large = new ConversationHistory(repository, "latest", counter, whitelist, inputTokens: 8000);
        var (smallHistory, smallTokens) = await small.GetForChatGpt(1, default);
        var (largeHistory, largeTokens) = await large.GetForChatGpt(1, default);

        Assert.Equal(2, smallHistory.Length - 2);
        Assert.Equal(6, largeHistory.Length - 2);
        Assert.Equal(messages.Reverse().Select(x => $"{x.User}: {x.Message}"), largeHistory.Skip(1).SkipLast(1).Select(x => x.Content));
        Assert.Equal("latest", largeHistory[^1].Content);
        Assert.True(smallTokens <= 2100 && largeTokens <= 8000);
    }

    [Fact]
    public async Task RejectsOversizedMandatoryInputWithoutDroppingCurrentMessage()
    {
        var counter = Substitute.For<ITokenCounter>();
        counter.Count(Arg.Any<string>()).Returns(x => ((string)x[0]).Length);
        var whitelist = Substitute.For<IWhitelist>();
        whitelist.GetSystemRoleMessage(1).Returns(new string('s', 100));
        var history = new ConversationHistory(Substitute.For<ITelegramMessageRepository>(),
            new string('u', 100), counter, whitelist, inputTokens: 128);

        await Assert.ThrowsAsync<InvalidOperationException>(() => history.GetForChatGpt(1, default));
        var settings = new CompletionSettings(inputTokens: 128);
        Assert.Throws<InvalidOperationException>(() => settings.CreateRequest(
            [ChatMessage.FromUser(new string('u', 200))], "chat", counter));
    }

    [Fact]
    public async Task SummarySplitsOversizedUnicodeInputWithinConfiguredBudget()
    {
        var counter = Substitute.For<ITokenCounter>();
        counter.Count(Arg.Any<string>()).Returns(x => ((string)x[0]).Length);
        var text = string.Concat(Enumerable.Repeat("😀", 200));
        var source = new[] { new TelegramMessage { User = "U", Message = text } };
        var batches = new List<ChatMessage[]>();

        await foreach (var batch in ChatMessageBuilder.CreateBatches(
            source.ToAsyncEnumerable(), "system", "summarize", counter, maxTokens: 128))
        {
            batches.Add(batch);
            var request = new CompletionSettings(inputTokens: 128).CreateRequest(batch, "summary", counter);
            Assert.All(request.Messages, m =>
            {
                Assert.False(char.IsHighSurrogate(m.Content![^1]));
                Assert.False(char.IsLowSurrogate(m.Content![0]));
            });
        }

        Assert.True(batches.Count > 1);
        Assert.Equal(text, string.Concat(batches.Select(x => x[1].Content!["U: ".Length..])));
    }

    [Fact]
    public async Task RepositoryAppliesConfiguredRetentionToNewMessages()
    {
        var cosmos = Substitute.For<CosmosClient>();
        var container = Substitute.For<Container>();
        cosmos.GetContainer("test", "messages").Returns(container);
        var repository = new TelegramMessageRepository(cosmos,
            Options.Create(new CosmosDbOptions { DatabaseId = "test", MessagesContainerId = "messages", MessageRetentionDays = 7 }),
            NullLogger<TelegramMessageRepository>.Instance);

        await repository.Create(new TelegramMessage { ChatId = 123, MessageId = 1, Message = "hello" }, default);

        await container.Received(1).UpsertItemAsync(
            Arg.Is<TelegramMessage>(m => m.TTL == 7 * 86400), Arg.Any<PartitionKey?>(),
            Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelegramMessageRepository(cosmos,
            Options.Create(new CosmosDbOptions { MessageRetentionDays = 0 }), NullLogger<TelegramMessageRepository>.Instance));
    }

    [Theory]
    [InlineData("stop", true)]
    [InlineData("length", false)]
    public async Task TldrUsesSummarySettingsAndDoesNotCacheTruncatedOutput(string finishReason, bool shouldCache)
    {
        var repository = Substitute.For<ITelegramMessageRepository>();
        repository.Get(123, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(
            new[] { new TelegramMessage { User = "friend", Message = "hello" } }.ToAsyncEnumerable());
        var client = Substitute.For<IOpenAIService>();
        client.ChatCompletion.CreateCompletion(Arg.Any<ChatCompletionCreateRequest>(), "test-summary", Arg.Any<CancellationToken>())
            .Returns(new ChatCompletionCreateResponse
            {
                Choices = [new ChatChoiceResponse { Message = ChatMessage.FromAssistant("summary"), FinishReason = finishReason }]
            });
        var cache = Substitute.For<ITimedTaskExecutionRepository>();
        cache.BeginTldr(123, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TldrCache());
        var whitelist = Substitute.For<IWhitelist>();
        whitelist.GetSystemRoleMessage(123).Returns("system");
        var settings = TestAzureSettings.Create(summary: new CompletionSettings(maxCompletionTokens: 4096, temperature: null));
        var command = new TooLongDidNotReadToday(repository, new TokenCounter(),
            new SummaryOpenAiClient(client, settings.SummaryDeployment, settings.Summary),
            NullLogger<TooLongDidNotReadToday>.Instance, Substitute.For<ITelegramBotClient>(), whitelist, cache, settings);

        await command.Execute(123);

        await client.ChatCompletion.Received(1).CreateCompletion(
            Arg.Is<ChatCompletionCreateRequest>(r => r.Model == "test-summary" && r.Temperature == null && r.MaxCompletionTokens == 4096),
            "test-summary", Arg.Any<CancellationToken>());
        await cache.Received(shouldCache ? 1 : 0).CompleteTldr(Arg.Any<TldrCache>(), Arg.Any<CancellationToken>());
    }
}

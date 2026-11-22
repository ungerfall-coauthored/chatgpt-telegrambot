using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Interfaces;
using OpenAI.ObjectModels.RequestModels;
using OpenAI.ObjectModels.ResponseModels;
using OpenAI.ObjectModels.SharedModels;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

[CollectionDefinition("Conversation policy environment", DisableParallelization = true)]
public sealed class ConversationPolicyCollection { }

[Collection("Conversation policy environment")]
public class NaturalRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BotOpinionRequestAfterAnotherHumanGetsOneContextualReply(bool replyToHuman)
    {
        var transport = new DecisionTransport(.99);
        using var http = new HttpClient(transport);
        using var scenario = new NaturalRequestScenario(new DecisionsClient(http, NullLogger<DecisionsClient>.Instance,
            new AzureDecisionsSettings("https://example.services.ai.azure.com/", "test-key", "decisions")));
        if (replyToHuman) scenario.State.AutonomousPending = true;

        await scenario.Run(replyToHuman);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, scenario.Budget.Decisions);
        Assert.Equal(3, scenario.Budget.AutonomousCandidates);
        Assert.False(scenario.State.AutonomousPending);
        using var body = JsonDocument.Parse(transport.Body!);
        Assert.Single(body.RootElement.GetProperty("questions").EnumerateObject());
        var state = body.RootElement.GetProperty("state").GetString()!;
        Assert.Contains(NaturalRequestScenario.FoodQuestion, state);
        Assert.Contains(NaturalRequestScenario.HumanSuggestion, state);
        await scenario.AssertOneReply();
    }

    [Fact]
    public async Task NaturalRequestUsesDirectRequestDecisionDuringContinuationWindow()
    {
        var transport = new DecisionTransport(.99);
        using var http = new HttpClient(transport);
        using var scenario = new NaturalRequestScenario(new DecisionsClient(http, NullLogger<DecisionsClient>.Instance,
            new AzureDecisionsSettings("https://example.services.ai.azure.com/", "test-key", "decisions")),
            new ConversationPolicyOptions { DecisionsEnabled = true, NaturalRequestsEnabled = true, ContinuationEnabled = true });
        scenario.State.UserId = 1;
        scenario.State.ExpiresUtc = DateTime.UtcNow.AddMinutes(1);
        scenario.State.RemainingCandidates = 1;

        await scenario.Handler.Handle(scenario.Update(3, NaturalRequestScenario.BotRequest), default);

        Assert.Equal(1, transport.Calls);
        Assert.Contains("explicitly requests an answer", transport.Body);
        await scenario.Bot.Received(1).SendRequest<Message>(
            Arg.Is<SendRichMessageRequest>(request => request.ReplyParameters != null && request.ReplyParameters.MessageId == 3),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 20)]
    public async Task DisabledOrExhaustedNaturalRequestsDoNotCallTheModel(bool naturalEnabled, bool decisionsEnabled, int used)
    {
        var transport = new DecisionTransport(.99);
        using var http = new HttpClient(transport);
        using var scenario = new NaturalRequestScenario(new DecisionsClient(http, NullLogger<DecisionsClient>.Instance,
            new AzureDecisionsSettings("https://example.services.ai.azure.com/", "test-key", "decisions")),
            new ConversationPolicyOptions { NaturalRequestsEnabled = naturalEnabled, DecisionsEnabled = decisionsEnabled });
        scenario.Budget.Decisions = used;

        await scenario.Handler.Handle(scenario.Update(3, NaturalRequestScenario.BotRequest), default);

        Assert.Equal(0, transport.Calls);
        await scenario.Bot.DidNotReceive().SendRequest<Message>(Arg.Any<SendRichMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MentioningBotsWithoutRequestingAnAnswerDoesNotBypassTheModel()
    {
        var transport = new DecisionTransport(.01);
        using var http = new HttpClient(transport);
        using var scenario = new NaturalRequestScenario(new DecisionsClient(http, NullLogger<DecisionsClient>.Instance,
            new AzureDecisionsSettings("https://example.services.ai.azure.com/", "test-key", "decisions")));

        await scenario.Handler.Handle(scenario.Update(1, "Мы вчера обсуждали бота, но сейчас разговариваем между собой."), default);

        Assert.Equal(1, transport.Calls);
        await scenario.Bot.DidNotReceive().SendRequest<Message>(Arg.Any<SendRichMessageRequest>(), Arg.Any<CancellationToken>());
    }

    private sealed class DecisionTransport(double probability) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    answers = new { should_reply = new { type = "noul", noul = probability } },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}

internal sealed class NaturalRequestScenario : IDisposable
{
    public const string FoodQuestion = "Жрать охота. У меня есть творог. Что бы такое приготовить";
    public const string HumanSuggestion = "Сырники";
    public const string BotRequest = "Неплохо, но хотелось бы знать мнение бота";
    private readonly long ChatId;
    private readonly string? _previousPolicy = Environment.GetEnvironmentVariable("ConversationPolicyChats");
    private readonly IOpenAIService _completion;
    public ITelegramBotClient Bot { get; }
    public ConversationState State { get; }
    public ConversationBudget Budget { get; }
    public UpdateHandler Handler { get; }

    public NaturalRequestScenario(DecisionsClient decisions, ConversationPolicyOptions? options = null,
        IOpenAIService? completion = null, AzureOpenAiSettings? azureSettings = null, string? systemPrompt = null,
        ITelegramBotClient? bot = null, long chatId = 123)
    {
        ChatId = chatId;
        Bot = bot ?? Substitute.For<ITelegramBotClient>();
        State = new ConversationState { ChatId = ChatId, Id = "conversation-topic:0" };
        Budget = new ConversationBudget
        {
            ChatId = ChatId, DateUtc = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AutonomousCandidates = 3, LastAutonomousUtc = DateTime.UtcNow,
        };
        _completion = completion ?? Substitute.For<IOpenAIService>();
        options ??= new ConversationPolicyOptions
        {
            DecisionsEnabled = true, NaturalRequestsEnabled = true,
            AutonomousEnabled = false, AutonomousProbability = 0, AutonomousDailyLimit = 0,
        };
        Environment.SetEnvironmentVariable("ConversationPolicyChats",
            JsonSerializer.Serialize(new Dictionary<long, ConversationPolicyOptions> { [ChatId] = options }));
        var repository = Substitute.For<IConversationRepository>();
        repository.Begin(ChatId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new ConversationReceipt { ChatId = ChatId, Id = $"conversation-message:{call.ArgAt<int>(1)}" });
        repository.Get(ChatId, 0, Arg.Any<CancellationToken>()).Returns(State);
        repository.GetBudget(ChatId, Arg.Any<CancellationToken>()).Returns(Budget);
        repository.Save(Arg.Any<ConversationState>(), Arg.Any<CancellationToken>()).Returns(true);
        repository.ReserveDecision(Arg.Any<ConversationReceipt>(), Arg.Any<ConversationState>(), Arg.Any<ConversationBudget>(), Arg.Any<CancellationToken>())
            .Returns(call => { call.ArgAt<ConversationReceipt>(0).DecisionAttempted = true; return true; });
        var messages = Substitute.For<ITelegramMessageRepository>();
        var tokens = Substitute.For<ITokenCounter>();
        var whitelist = Substitute.For<IWhitelist>();
        whitelist.IsGroupAllowedToUseBot(ChatId).Returns(true);
        whitelist.GetSystemRoleMessage(ChatId).Returns(systemPrompt ?? "Answer the user's cooking question.");
        if (completion == null)
            _completion.ChatCompletion.CreateCompletion(Arg.Any<ChatCompletionCreateRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new ChatCompletionCreateResponse
                {
                    Choices = [new ChatChoiceResponse { Message = ChatMessage.FromAssistant("Из творога можно приготовить ленивые вареники.") }],
                });
        if (bot == null)
            Bot.SendRequest<Message>(Arg.Any<SendRichMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message
            {
                Id = 100, Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup }, Date = DateTime.UtcNow,
                From = new User { Id = 999, FirstName = "Bot", IsBot = true, Username = "chatgpt_ungerfall_bot" },
            });
        var azure = azureSettings ?? TestAzureSettings.Create();
        Handler = new UpdateHandler(Bot, NullLogger<UpdateHandler>.Instance, _completion, messages, tokens,
            new TooLongDidNotReadToday(messages, tokens, new SummaryOpenAiClient(_completion, "test-summary"),
                NullLogger<TooLongDidNotReadToday>.Instance, Bot, whitelist, Substitute.For<ITimedTaskExecutionRepository>(), azure),
            whitelist, new GenerateImage(new ImageOpenAiClient(_completion), NullLogger<GenerateImage>.Instance, Bot),
            new TestUsers(), repository,
            new ConversationPolicy(repository, new ConversationPolicyChats(), NullLogger<ConversationPolicy>.Instance, decisions),
            new PersonalSettings(Bot, NullLogger<PersonalSettings>.Instance, azure), azure);
    }

    public Update Update(int id, string text) => new()
    {
        Message = new Message
        {
            Id = id, Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup }, Text = text, Date = DateTime.UtcNow,
            From = new User { Id = id == 2 ? 2 : 1, FirstName = id == 2 ? "Tigran" : "User" },
        },
    };

    public async Task Run(bool replyToHuman = false)
    {
        await Handler.Handle(Update(1, FoodQuestion), default);
        var suggestion = Update(2, HumanSuggestion);
        await Handler.Handle(suggestion, default);
        await AssertNoReply();
        var request = Update(3, BotRequest);
        if (replyToHuman) request.Message!.ReplyToMessage = suggestion.Message;
        await Handler.Handle(request, default);
    }

    public async Task AssertOneReply()
    {
        await Bot.Received(1).SendRequest<Message>(
            Arg.Is<SendRichMessageRequest>(request => request.ReplyParameters != null && request.ReplyParameters.MessageId == 3),
            Arg.Any<CancellationToken>());
        await _completion.ChatCompletion.Received(1).CreateCompletion(
            Arg.Is<ChatCompletionCreateRequest>(request => request.Messages.Any(message =>
                message.Content != null && message.Content.Contains(FoodQuestion))),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    public Task AssertSentOneReply() =>
        Bot.Received(1).SendRequest<Message>(
            Arg.Is<SendRichMessageRequest>(request => request.ReplyParameters != null && request.ReplyParameters.MessageId == 3),
            Arg.Any<CancellationToken>());

    public Task AssertNoReply() =>
        Bot.DidNotReceive().SendRequest<Message>(Arg.Any<SendRichMessageRequest>(), Arg.Any<CancellationToken>());

    public void Dispose() => Environment.SetEnvironmentVariable("ConversationPolicyChats", _previousPolicy);
}

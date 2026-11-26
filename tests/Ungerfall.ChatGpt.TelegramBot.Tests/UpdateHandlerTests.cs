using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;
public class UpdateHandlerTest
{
    [Fact]
    public async Task Handle_ShouldAwaitHistoryAndPropagateError_WhenDatabaseWriteFails()
    {
        // Arrange
        const long chatId = 123;
        var botClientMock = Substitute.For<ITelegramBotClient>();
        var loggerMock = Substitute.For<ILogger<UpdateHandler>>();
        var chatClientMock = Substitute.For<IChatCompletionClient>();
        var imageClientMock = Substitute.For<IImageGenerationClient>();
        var messageRepoMock = Substitute.For<ITelegramMessageRepository>();
        var tokenCounterMock = Substitute.For<ITokenCounter>();
        var whitelistMock = Substitute.For<IWhitelist>();
        var conversationRepoMock = Substitute.For<IConversationRepository>();
        conversationRepoMock.Begin(chatId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationReceipt());
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new InvalidOperationException("Database unavailable");
        var update = new Update
        {
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = chatId, Type = ChatType.Group },
                Text = "Hello",
            }
        };

        whitelistMock.IsGroupAllowedToUseBot(chatId).Returns(true);
        messageRepoMock.Create(Arg.Any<TelegramMessage>(), Arg.Any<CancellationToken>())
            .Returns(saved.Task);

        var handler = new UpdateHandler(
            botClientMock, loggerMock, chatClientMock, messageRepoMock, tokenCounterMock,
            new TooLongDidNotReadToday(messageRepoMock, tokenCounterMock, new SummaryOpenAiClient(chatClientMock, "test-summary"),
                Substitute.For<ILogger<TooLongDidNotReadToday>>(), botClientMock, whitelistMock, Substitute.For<ITimedTaskExecutionRepository>(), TestAzureSettings.Create()),
            whitelistMock,
            new GenerateImage(new ImageOpenAiClient(imageClientMock), Substitute.For<ILogger<GenerateImage>>(), botClientMock),
            new TestUsers(), conversationRepoMock,
            new ConversationPolicy(conversationRepoMock, new ConversationPolicyChats(), Substitute.For<ILogger<ConversationPolicy>>(),
                new DecisionsClient(new HttpClient(), Substitute.For<ILogger<DecisionsClient>>(), new AzureDecisionsSettings())),
            new PersonalSettings(botClientMock, Substitute.For<ILogger<PersonalSettings>>(), TestAzureSettings.Create()),
            TestAzureSettings.Create());

        // Act
        var handling = handler.Handle(update, default);
        var completedBeforeSave = handling.IsCompleted;
        saved.SetException(error);
        var exception = await Record.ExceptionAsync(() => handling);

        // Assert
        Assert.False(completedBeforeSave);
        Assert.Same(error, exception);
        await messageRepoMock.Received(1).Create(Arg.Any<TelegramMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldRestoreBotResponseAsAssistant_WhenHistoryIsRead()
    {
        // Arrange
        const long chatId = 123;
        var botClientMock = Substitute.For<ITelegramBotClient>();
        var loggerMock = Substitute.For<ILogger<UpdateHandler>>();
        var chatClientMock = Substitute.For<IChatCompletionClient>();
        var imageClientMock = Substitute.For<IImageGenerationClient>();
        var messageRepoMock = Substitute.For<ITelegramMessageRepository>();
        var tokenCounterMock = Substitute.For<ITokenCounter>();
        var whitelistMock = Substitute.For<IWhitelist>();
        var conversationRepoMock = Substitute.For<IConversationRepository>();
        conversationRepoMock.Begin(chatId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ConversationReceipt());
        var messages = new List<TelegramMessage>();
        var chat = new Chat { Id = chatId, Type = ChatType.Private };
        var sent = new Message
        {
            Id = 2,
            Chat = chat,
            Date = DateTime.UtcNow,
            From = new User { Id = 456, IsBot = true, FirstName = "Bot", Username = "chatgpt_ungerfall_bot" },
        };
        var update = new Update
        {
            Message = new Message
            {
                Id = 1,
                Chat = chat,
                Text = "Hello",
                Date = DateTime.UtcNow,
                From = new User { Id = 789, FirstName = "User" },
            }
        };

        whitelistMock.IsGroupAllowedToUseBot(chatId).Returns(true);
        whitelistMock.GetSystemRoleMessage(chatId).Returns("System role");
        messageRepoMock.GetAllOrderByDateDescending(chatId, Arg.Any<CancellationToken>())
            .Returns(_ => messages.AsEnumerable().Reverse().ToAsyncEnumerable());
        messageRepoMock.Create(Arg.Do<TelegramMessage>(messages.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        chatClientMock.CompleteAsync(Arg.Any<ChatCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChatCompletionResult(true, "Assistant reply", "stop"));
        botClientMock.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(sent);

        var handler = new UpdateHandler(
            botClientMock, loggerMock, chatClientMock, messageRepoMock, tokenCounterMock,
            new TooLongDidNotReadToday(messageRepoMock, tokenCounterMock, new SummaryOpenAiClient(chatClientMock, "test-summary"),
                Substitute.For<ILogger<TooLongDidNotReadToday>>(), botClientMock, whitelistMock, Substitute.For<ITimedTaskExecutionRepository>(), TestAzureSettings.Create()),
            whitelistMock,
            new GenerateImage(new ImageOpenAiClient(imageClientMock), Substitute.For<ILogger<GenerateImage>>(), botClientMock),
            new TestUsers(), conversationRepoMock,
            new ConversationPolicy(conversationRepoMock, new ConversationPolicyChats(), Substitute.For<ILogger<ConversationPolicy>>(),
                new DecisionsClient(new HttpClient(), Substitute.For<ILogger<DecisionsClient>>(), new AzureDecisionsSettings())),
            new PersonalSettings(botClientMock, Substitute.For<ILogger<PersonalSettings>>(), TestAzureSettings.Create()),
            TestAzureSettings.Create());

        // Act
        await handler.Handle(update, default);
        var history = new ConversationHistory(messageRepoMock, "Next message", tokenCounterMock, whitelistMock);
        var (gptMessages, _) = await history.GetForChatGpt(chatId, default);

        // Assert
        var botMessage = Assert.Single(messages, m => m.MessageId == sent.Id);
        Assert.Equal("chatgpt_ungerfall_bot", botMessage.User);
        Assert.Equal(sent.From.Id, botMessage.UserId);
        var assistant = Assert.Single(gptMessages, m => m.Role == "assistant");
        Assert.Equal("Assistant reply", assistant.Content);
    }

    [Fact]
    public async Task Handle_ShouldRecordPhotoActivityWithCaption()
    {
        const long chatId = 123;
        var bot = Substitute.For<ITelegramBotClient>();
        var repository = Substitute.For<ITelegramMessageRepository>();
        var whitelist = Substitute.For<IWhitelist>();
        var events = new List<TelegramMessage>();
        whitelist.IsGroupAllowedToUseBot(chatId).Returns(true);
        repository.Create(Arg.Do<TelegramMessage>(events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var handler = CreateHandler(bot, repository, whitelist, Substitute.For<IConversationRepository>());

        await handler.Handle(new Update
        {
            Message = new Message
            {
                Id = 42,
                Chat = new Chat { Id = chatId, Type = ChatType.Group },
                Date = DateTime.UtcNow,
                From = new User { Id = 456, FirstName = "Alice" },
                Photo = [new PhotoSize { FileId = "photo", FileUniqueId = "photo-unique" }],
                Caption = "The new menu",
            },
        }, default);

        var entry = Assert.Single(events);
        Assert.Equal("Alice", entry.User);
        Assert.Equal("[activity] sent an image; caption: The new menu", entry.Message);
        await bot.DidNotReceive().SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldRecordEmptyTextAsActivity()
    {
        const long chatId = 123;
        var repository = Substitute.For<ITelegramMessageRepository>();
        var whitelist = Substitute.For<IWhitelist>();
        var events = new List<TelegramMessage>();
        whitelist.IsGroupAllowedToUseBot(chatId).Returns(true);
        repository.Create(Arg.Do<TelegramMessage>(events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var handler = CreateHandler(Substitute.For<ITelegramBotClient>(), repository, whitelist,
            Substitute.For<IConversationRepository>());

        await handler.Handle(new Update
        {
            Message = new Message
            {
                Id = 42,
                Chat = new Chat { Id = chatId, Type = ChatType.Group },
                Date = DateTime.UtcNow,
                From = new User { Id = 456, FirstName = "Alice" },
                Text = string.Empty,
            },
        }, default);

        Assert.Equal("[activity] sent an empty message", Assert.Single(events).Message);
    }

    [Fact]
    public async Task Handle_ShouldRecordImageCommandAsActivity()
    {
        const long chatId = 123;
        var bot = Substitute.For<ITelegramBotClient>();
        var repository = Substitute.For<ITelegramMessageRepository>();
        var whitelist = Substitute.For<IWhitelist>();
        var conversations = Substitute.For<IConversationRepository>();
        var imageClient = Substitute.For<IImageGenerationClient>();
        var events = new List<TelegramMessage>();
        var sent = new Message { Id = 43, Chat = new Chat { Id = chatId, Type = ChatType.Group } };
        whitelist.IsGroupAllowedToUseBot(chatId).Returns(true);
        conversations.Begin(chatId, 42, Arg.Any<CancellationToken>()).Returns(new ConversationReceipt());
        conversations.SaveReceipt(Arg.Any<ConversationReceipt>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        repository.Create(Arg.Do<TelegramMessage>(events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        imageClient.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ImageGenerationResult(true, "aGVsbG8=", null));
        bot.SendRequest<Message>(Arg.Any<SendPhotoRequest>(), Arg.Any<CancellationToken>()).Returns(sent);
        var handler = CreateHandler(bot, repository, whitelist, conversations, imageClient: imageClient);

        await handler.Handle(new Update
        {
            Message = new Message
            {
                Id = 42,
                Chat = new Chat { Id = chatId, Type = ChatType.Group },
                Date = DateTime.UtcNow,
                From = new User { Id = 456, FirstName = "Alice" },
                Text = "/image a fox",
            },
        }, default);

        var entry = Assert.Single(events);
        Assert.Equal("[activity] requested image generation: a fox", entry.Message);
        await imageClient.Received(1).GenerateAsync(" a fox", Arg.Any<CancellationToken>());
    }

    private static UpdateHandler CreateHandler(
        ITelegramBotClient bot,
        ITelegramMessageRepository repository,
        IWhitelist whitelist,
        IConversationRepository conversations,
        IImageGenerationClient? imageClient = null)
    {
        var tokenCounter = Substitute.For<ITokenCounter>();
        var azure = TestAzureSettings.Create();
        return new UpdateHandler(
            bot,
            Substitute.For<ILogger<UpdateHandler>>(),
            Substitute.For<IChatCompletionClient>(),
            repository,
            tokenCounter,
            new TooLongDidNotReadToday(repository, tokenCounter,
                new SummaryOpenAiClient(Substitute.For<IChatCompletionClient>(), "test-summary"),
                Substitute.For<ILogger<TooLongDidNotReadToday>>(), bot, whitelist,
                Substitute.For<ITimedTaskExecutionRepository>(), azure),
            whitelist,
            new GenerateImage(new ImageOpenAiClient(imageClient), Substitute.For<ILogger<GenerateImage>>(), bot),
            new TestUsers(),
            conversations,
            new ConversationPolicy(conversations, new ConversationPolicyChats(), Substitute.For<ILogger<ConversationPolicy>>(),
                new DecisionsClient(new HttpClient(), Substitute.For<ILogger<DecisionsClient>>(), new AzureDecisionsSettings())),
            new PersonalSettings(bot, Substitute.For<ILogger<PersonalSettings>>(), azure),
            azure);
    }
}

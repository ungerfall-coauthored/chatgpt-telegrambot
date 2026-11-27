using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Storage;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot;

public class UpdateHandler
{
    private const string ActivityHistoryPrefix = "[activity] ";
    private readonly string BotUsername = "@" + (Environment.GetEnvironmentVariable("TELEGRAM_BOT_USERNAME") ?? "chatgpt_ungerfall_bot").TrimStart('@');

    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly IChatCompletionClient _chatClient;
    private readonly ITelegramMessageRepository _telegramMessagesRepository;
    private readonly ITokenCounter _tokenCounter;
    private readonly TooLongDidNotReadToday _tooLongDidnotReadCommand;
    private readonly GenerateImage _imageCommand;
    private readonly PersonalSettings _personalSettingsCommand;
    private readonly IWhitelist _whitelist;
    private readonly TestUsers _testUsers;
    private readonly IConversationRepository _conversations;
    private readonly ConversationPolicy _conversationPolicy;
    private readonly AzureOpenAiSettings _azureOpenAi;
    private readonly PromptCatalog _prompts;
    private readonly DockingGame _dockingGame;
    private readonly HistoryPrivacy? _historyPrivacy;

    public UpdateHandler(
        ITelegramBotClient botClient,
        ILogger<UpdateHandler> logger,
        IChatCompletionClient chatClient,
        ITelegramMessageRepository telegramMessagesRepository,
        ITokenCounter tokenCounter,
        TooLongDidNotReadToday tooLongDidnotReadCommand,
        IWhitelist whitelist,
        GenerateImage imageCommand,
        TestUsers testUsers,
        IConversationRepository conversations,
        ConversationPolicy conversationPolicy,
        PersonalSettings personalSettingsCommand,
        AzureOpenAiSettings azureOpenAi,
        PromptCatalog? prompts = null, HistoryPrivacy? historyPrivacy = null)
    {
        _botClient = botClient;
        _logger = logger;
        _chatClient = chatClient;
        _telegramMessagesRepository = telegramMessagesRepository;
        _tokenCounter = tokenCounter;
        _tooLongDidnotReadCommand = tooLongDidnotReadCommand;
        _whitelist = whitelist;
        _imageCommand = imageCommand;
        _testUsers = testUsers;
        _conversations = conversations;
        _conversationPolicy = conversationPolicy;
        _personalSettingsCommand = personalSettingsCommand;
        _azureOpenAi = azureOpenAi;
        _prompts = prompts ?? PromptCatalog.Load();
        _dockingGame = new DockingGame(botClient, logger);
        _historyPrivacy = historyPrivacy;
    }

    public async Task Handle(Update update, CancellationToken cancellation)
    {
        _logger.LogInformation("Invoke telegram update function");
        if (update.Message is { From: { IsBot: false } author, Text: { } command } privacyMessage
            && _historyPrivacy?.Allows(privacyMessage.Chat.Id) == true && privacyMessage.Id > 0
            && (command.Trim() is "/historyconsent" or "/historyforget"
                || command.Trim().Equals("/historyconsent" + BotUsername, StringComparison.OrdinalIgnoreCase)
                || command.Trim().Equals("/historyforget" + BotUsername, StringComparison.OrdinalIgnoreCase)))
        {
            var forget = command.Trim().Split('@')[0] == "/historyforget";
            if (forget) await _historyPrivacy.Forget(privacyMessage.Chat.Id, author.Id, cancellation);
            else await _historyPrivacy.Grant(privacyMessage.Chat.Id, author.Id, cancellation);
            await _botClient.SendResponse(privacyMessage.Chat.Id,
                forget ? "History consent revoked; archived originals and author metadata removed. Index cleanup may continue asynchronously. Minimal opt-out and anonymous tombstones remain to prevent re-import."
                    : "History consent enabled for future messages. Existing opt-out tombstones are not restored.",
                replyParameters: privacyMessage.Id, messageThreadId: privacyMessage.MessageThreadId, cancellationToken: cancellation);
            return;
        }

        var handler = update switch
        {
            /*
             { EditedMessage: { } }      => UpdateType.EditedMessage,
             { InlineQuery: { } }        => UpdateType.InlineQuery,
             { ChosenInlineResult: { } } => UpdateType.ChosenInlineResult,
             { CallbackQuery: { } }      => UpdateType.CallbackQuery,
             { ChannelPost: { } }        => UpdateType.ChannelPost,
             { EditedChannelPost: { } }  => UpdateType.EditedChannelPost,
             { ShippingQuery: { } }      => UpdateType.ShippingQuery,
             { PreCheckoutQuery: { } }   => UpdateType.PreCheckoutQuery,
             { Poll: { } }               => UpdateType.Poll,
             { PollAnswer: { } }         => UpdateType.PollAnswer,
             { MyChatMember: { } }       => UpdateType.MyChatMember,
             { ChatMember: { } }         => UpdateType.ChatMember,
             { ChatJoinRequest: { } }    => UpdateType.ChatJoinRequest,
            */
            { CallbackQuery: { GameShortName: { } game } query } when _dockingGame.Matches(game)
                => BotOnGameCallback(query, cancellation),
            { CallbackQuery: { Data: { } data } query } when data.StartsWith("settings:", StringComparison.Ordinal)
                => BotOnCallbackQueryReceived(query, cancellation),
            { Message: { } message } => BotOnMessageReceived(message, cancellation),
            _ => UnknownUpdateHandlerAsync(update, cancellation)
        };

        await handler;
    }

    private async Task BotOnMessageReceived(Message message, CancellationToken cancellation)
    {
        if (message.EphemeralMessageId != null)
        {
            // Ephemeral commands have message_id = 0 and must never enter shared history or receipts.
            var command = message.Text?.Split(' ', 2)[0];
            if (message.Chat.Type is ChatType.Group or ChatType.Supergroup
                && message.From is { IsBot: false } && !IsBot(message.From)
                && _whitelist.IsGroupAllowedToUseBot(message.Chat.Id)
                && (string.Equals(command, "/settings", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(command, $"/settings{BotUsername}", StringComparison.OrdinalIgnoreCase)))
                await _personalSettingsCommand.Execute(message, cancellation);
            return;
        }
        _logger.LogInformation("Receive message {@message}", new { message.Text, message.Type, message.Chat.Id });
        if (message.From?.IsBot == true || IsBot(message.From))
            return;

        bool allowed = _whitelist.IsGroupAllowedToUseBot(message.Chat.Id)
            || _testUsers.Get().Contains(message.Chat.Id);
        if (string.IsNullOrWhiteSpace(message.Text))
        {
            if (!allowed || message.From == null) return;
            await SaveToHistory(message.Chat.Id, message.From.Id, DescribeNonTextMessage(message), message.Id,
                message.Date, message.From.FirstName ?? message.From.Username ?? "unknown", cancellation, message.MessageThreadId);
            return;
        }
        if (!allowed)
        {
            await _botClient.SendMessage(
                chatId: message.Chat.Id,
                text: $"""
                Only for my chat.
                You:
                ```{JsonSerializer.Serialize(message.Chat, typeof(Chat), SourceGenerators.TelegramChatContext.Default)}```
                """,
                parseMode: ParseMode.Markdown,
                replyParameters: message.Id,
                cancellationToken: cancellation);
            return;
        }

        var messageText = message.Text;
        var user = message.From?.FirstName ?? message.From?.Username ?? "unknown";
        var receipt = await _conversations.Begin(message.Chat.Id, message.Id, cancellation);
        if (receipt == null) return;
        try
        {
            int commandEndIndex = messageText.GetCommandEndIndex();
            string command = messageText[0] == '/' ? messageText[..commandEndIndex].ToLowerInvariant() : string.Empty;
            bool otherBotCommand = false;
            if (command.Length > 0 && commandEndIndex < messageText.Length && messageText[commandEndIndex] == '@')
            {
                var targetEnd = messageText.IndexOf(' ', commandEndIndex);
                if (targetEnd < 0) targetEnd = messageText.Length;
                otherBotCommand = !messageText[commandEndIndex..targetEnd].Equals(BotUsername, StringComparison.OrdinalIgnoreCase);
                commandEndIndex = targetEnd;
                if (otherBotCommand) command = string.Empty;
            }

            async Task BeforeSend()
            {
                receipt.Stage = Database.ConversationReceipt.Sending;
                try
                {
                    await _conversations.SaveReceipt(receipt, cancellation);
                }
                catch
                {
                    receipt.Stage = Database.ConversationReceipt.Processing;
                    throw;
                }
            }

            string msgWithoutCommand = messageText[commandEndIndex..];
            var activity = DescribeCommandActivity(command, msgWithoutCommand);
            if (activity != null)
                await SaveToHistory(message.Chat.Id, message.From?.Id ?? 0, activity, message.Id, message.Date,
                    user, cancellation, message.MessageThreadId);

            var action = command switch
            {
                "/image" => _imageCommand.Execute(message, msgWithoutCommand, cancellation, BeforeSend),
                "/settings" => _personalSettingsCommand.Execute(message, cancellation, BeforeSend),
                "/systemprompt" => SendSystemPrompt(message, BeforeSend, cancellation),
                "/docking" => _dockingGame.Execute(message, BeforeSend, cancellation),
                "/tldrtoday" => _tooLongDidnotReadCommand.Execute(
                    message.Chat.Id, cancellation,
                    useRich: message.Chat.Type is ChatType.Group or ChatType.Supergroup,
                    messageThreadId: message.MessageThreadId,
                    beforeSend: BeforeSend),
                _ => OnMessageReceived(message, messageText, command.Length > 0, otherBotCommand, receipt, BeforeSend, cancellation),
            };
            await action;
            if (command is "/image" or "/tldrtoday")
            {
                await _conversationPolicy.Observe(message, null, null, cancellation, addressed: true);
            }
            receipt.Stage = Database.ConversationReceipt.Completed;
            await _conversations.SaveReceipt(receipt, cancellation);
        }
        catch (Exception error)
        {
            if (error is TelegramDeliveryRejectedException && receipt.Stage == Database.ConversationReceipt.Sending)
            {
                receipt.Stage = Database.ConversationReceipt.Processing;
                _logger.LogWarning(error, "Telegram rejected the first send for ({ChatId}, {MessageId}); releasing for retry", message.Chat.Id, message.Id);
            }
            if (receipt.Stage == Database.ConversationReceipt.Processing)
            {
                try
                {
                    await _conversations.Release(receipt, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to release message ({ChatId}, {MessageId}) for retry", message.Chat.Id, message.Id);
                }
            }
            throw;
        }
    }

    private static string DescribeNonTextMessage(Message message)
    {
        var activity = message.Type switch
        {
            MessageType.Photo => "sent an image",
            MessageType.Audio => "sent audio/music",
            MessageType.Voice => "sent a voice message",
            MessageType.Video => "sent a video",
            MessageType.VideoNote => "sent a video note",
            MessageType.Animation => "sent an animation",
            MessageType.Document => "sent a file",
            MessageType.Sticker => "sent a sticker",
            MessageType.Contact => "shared a contact",
            MessageType.Location => "shared a location",
            MessageType.Venue => "shared a place",
            MessageType.Poll => "created a poll",
            MessageType.Dice => "sent a dice roll",
            MessageType.Game => "shared a game",
            MessageType.Text => "sent an empty message",
            _ => "sent a non-text message",
        };
        var caption = string.IsNullOrWhiteSpace(message.Caption) ? string.Empty : $"; caption: {message.Caption}";
        return $"{ActivityHistoryPrefix}{activity}{caption}";
    }

    private static string? DescribeCommandActivity(string command, string commandText) => command switch
    {
        "/image" => string.IsNullOrWhiteSpace(commandText)
            ? $"{ActivityHistoryPrefix}requested an image"
            : $"{ActivityHistoryPrefix}requested image generation: {commandText.Trim()}",
        "/settings" => $"{ActivityHistoryPrefix}opened bot settings",
        "/systemprompt" => $"{ActivityHistoryPrefix}requested the bot's system prompt",
        "/docking" => $"{ActivityHistoryPrefix}started the docking game",
        "/tldrtoday" => $"{ActivityHistoryPrefix}requested a TL;DR of today's chat",
        _ => null,
    };

    private async Task SendSystemPrompt(Message message, Func<Task> beforeSend, CancellationToken cancellation)
    {
        if (message.Chat.Type is not (ChatType.Group or ChatType.Supergroup)
            || !_whitelist.IsGroupAllowedToUseBot(message.Chat.Id)) return;
        await beforeSend();
        await _botClient.SendResponse(message.Chat.Id, _whitelist.GetSystemRoleMessage(message.Chat.Id),
            replyParameters: message.Id, messageThreadId: message.MessageThreadId, cancellationToken: cancellation);
    }

    private Task BotOnCallbackQueryReceived(CallbackQuery query, CancellationToken cancellation)
    {
        var chat = query.Message?.Chat;
        bool allowed = chat != null && (chat.Type == ChatType.Private
            ? chat.Id == query.From.Id && _testUsers.Get().Contains(chat.Id)
            : chat.Type is ChatType.Group or ChatType.Supergroup && _whitelist.IsGroupAllowedToUseBot(chat.Id));
        return allowed ? _personalSettingsCommand.Execute(query, cancellation) : Task.CompletedTask;
    }

    private Task BotOnGameCallback(CallbackQuery query, CancellationToken cancellation)
    {
        var chat = query.Message?.Chat;
        bool allowed = !query.From.IsBot && chat != null && (chat.Type == ChatType.Private
            ? chat.Id == query.From.Id && _testUsers.Get().Contains(chat.Id)
            : chat.Type is ChatType.Group or ChatType.Supergroup && _whitelist.IsGroupAllowedToUseBot(chat.Id));
        return _dockingGame.Answer(query, allowed, cancellation);
    }

    private async Task<Message> OnMessageReceived(
        Message msg,
        string messageText,
        bool isCommand,
        bool otherBotCommand,
        Database.ConversationReceipt receipt,
        Func<Task> beforeSend,
        CancellationToken cancellation)
    {
        bool activateBot = !otherBotCommand && (msg.Chat.Type == ChatType.Private || isCommand
            || IsBot(msg.ReplyToMessage?.From)
            || ((msg.Entities?.Any(x => x.Type == MessageEntityType.Mention) ?? false)
                && (msg.EntityValues?.Any(x => x.Equals(BotUsername, StringComparison.OrdinalIgnoreCase)) ?? false))
            || (msg.Entities?.Any(x => x.Type == MessageEntityType.TextMention && IsBot(x.User)) ?? false));
        var user = msg.From?.FirstName ?? msg.From?.Username ?? "unknown";
        var chatId = msg.Chat.Id;
        var decision = activateBot ? ConversationDecision.Continuation : otherBotCommand ? ConversationDecision.Ignore
            : await _conversationPolicy.Decide(msg, receipt, cancellation);
        if (decision == ConversationDecision.Ignore)
        {
            _logger.LogInformation("Bot action is not expected");
            await SaveToHistory(chatId, UserId(msg), msg.Text!, msg.Id, msg.Date, user, cancellation, msg.MessageThreadId);
            await _conversationPolicy.Observe(msg, null, null, cancellation);
            return msg;
        }

        await _botClient.SendChatAction(
            chatId: chatId,
            ChatAction.Typing,
            cancellationToken: cancellation);
        var autonomous = decision == ConversationDecision.Interjection;
        var (chatGptResponse, tokens) = await SendChatGptMessage(messageText, user, chatId, cancellation,
            !activateBot ? receipt.DecisionInput : null, autonomous: autonomous, silentOnFailure: !activateBot,
            messageThreadId: msg.MessageThreadId,
            storageContext: !autonomous && msg.From != null
                ? new StorageContext(chatId, msg.From.Id, msg.Id, messageText, msg.MessageThreadId) : null);
        if (!activateBot && (string.IsNullOrWhiteSpace(chatGptResponse)
            || autonomous && !await _conversationPolicy.TryMarkInterjection(msg, cancellation)))
        {
            receipt.Decision = ConversationDecision.Ignore;
            await _conversations.SaveReceipt(receipt, cancellation);
            await SaveToHistory(chatId, UserId(msg), msg.Text!, msg.Id, msg.Date, user, cancellation, msg.MessageThreadId);
            await _conversationPolicy.Observe(msg, null, null, cancellation);
            return msg;
        }
        await beforeSend();
        var sent = await _botClient.SendResponse(
            chatId: chatId,
            text: autonomous ? chatGptResponse : $"{chatGptResponse}{Environment.NewLine}tokens: {tokens}",
            useRich: msg.Chat.Type is ChatType.Group or ChatType.Supergroup,
            replyParameters: msg.Id,
            messageThreadId: msg.MessageThreadId,
            cancellationToken: cancellation);
        await SaveToHistory(chatId, UserId(msg), msg.Text!, msg.Id, msg.Date, user, cancellation, msg.MessageThreadId);
        await SaveToHistory(chatId, UserId(sent), chatGptResponse, sent.Id, sent.Date, BotUsername[1..], cancellation, msg.MessageThreadId);
        await _conversationPolicy.Observe(msg, sent, chatGptResponse, cancellation, autonomous: autonomous,
            addressed: activateBot || decision == ConversationDecision.NaturalRequest);
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sent.Id);
        return sent;

        static long UserId(Message msg) => msg.From?.Id ?? default;
    }

    private bool IsBot(User? user) => user != null
        && ((_botClient.BotId != 0 && user.Id == _botClient.BotId)
            || string.Equals(user.Username, BotUsername[1..], StringComparison.OrdinalIgnoreCase));

    private async Task SaveToHistory(
        long chatId,
        long userId,
        string message,
        int messageId,
        DateTime date,
        string user,
        CancellationToken cancellation,
        int? messageThreadId = default)
    {
        await _telegramMessagesRepository.Create(
            new Database.TelegramMessage
            {
                ChatId = chatId,
                User = user,
                UserId = userId,
                Message = message,
                MessageId = messageId,
                MessageThreadId = messageThreadId,
                Date = date,
            },
            cancellation);
    }

    private async Task<(string, int?)> SendChatGptMessage(
        string message, string user, long chatId, CancellationToken cancellation,
        string? decisionContext = default, bool autonomous = false, bool silentOnFailure = false, int? messageThreadId = default,
        StorageContext? storageContext = null)
    {
        _logger.LogInformation("Sending {Message} from {User}", message, user);

        ChatMessage[] gptMessage;
        int tokensCount;
        if (decisionContext != null)
        {
            gptMessage = new[]
            {
                ChatMessage.FromSystem(_prompts.Chat.UntrustedDataPrefix
                    + (autonomous
                        ? _prompts.Chat.Autonomous
                        : _whitelist.GetSystemRoleMessage(chatId) + _prompts.Chat.Continuation)),
                ChatMessage.FromUser(decisionContext),
            };
            tokensCount = _tokenCounter.Count(decisionContext);
        }
        else
        {
            var history = new ConversationHistory(_telegramMessagesRepository, $"{user}: {message}", _tokenCounter, _whitelist,
                messageThreadId, _azureOpenAi.Chat.InputTokens);
            (gptMessage, tokensCount) = await history.GetForChatGpt(chatId, cancellation);
        }
        var completionResult = await _chatClient.CompleteAsync(
            _azureOpenAi.Chat.CreateRequest(gptMessage, _azureOpenAi.ChatDeployment, _tokenCounter, user,
                maxCompletionTokens: autonomous ? _azureOpenAi.AutonomousMaxCompletionTokens : null)
                with { StorageContext = storageContext },
            cancellation);
        _logger.LogInformation("Tokens: {@tokens}",
            new
            {
                MyTokensCounter = tokensCount,
                completionResult.CompletionTokens,
                completionResult.PromptTokens,
                completionResult.TotalTokens
            });
        if (completionResult.Successful)
        {
            return (completionResult.Content ?? (silentOnFailure ? string.Empty : "Successful, but no content."), completionResult.TotalTokens);
        }
        else
        {
            _logger.LogError("ChatGPT error: {@error}", new { completionResult.ErrorType, completionResult.ErrorMessage });
            var errorResponse = completionResult.ErrorType switch
            {
                "tool_budget_exceeded" => "Слишком тяжело. Контекст переполнен",
                "tool_query_recovery_exhausted" => "Не удалось исправить запрос к хранилищу.",
                "tool_call_limit" or "tool_round_limit" => "Не удалось завершить операции с хранилищем за один запрос. Некоторые изменения могли уже сохраниться.",
                _ => "ChatGPT request wasn't successful",
            };
            return (silentOnFailure ? string.Empty : errorResponse, completionResult.TotalTokens);
        }
    }

    private Task UnknownUpdateHandlerAsync(Update update, CancellationToken _)
    {
        _logger.LogInformation("Unknown update type: {UpdateType}", update.Type);
        return Task.CompletedTask;
    }

    public async Task HandlePollingErrorAsync(Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogInformation("HandleError: {ErrorMessage}", errorMessage);

        // Cooldown in case of network connection error
        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}

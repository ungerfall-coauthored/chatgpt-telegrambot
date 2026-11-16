using Microsoft.Extensions.Logging;
using OpenAI.Interfaces;
using OpenAI.ObjectModels.RequestModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.TimedTasks;
public class DailySummary : TimedTask
{
    public const string CRON_EXPRESSION = "0 55 23 * * *";
    private const int MaxConcurrentRequests = 4;

    // TODO: move to config/db
    private const string Prompt = """
            Напиши краткое содержание переписки.
            Если есть неотвеченные вопросы, перечисли их и ответь.
            В конце с новой строки добавь самые релевантные #теги переписки до 5 штук.
            """;
    private const float Temperature = .2f;

    private readonly DailySummaryChats _chats;
    private readonly IWhitelist _whitelist;
    private readonly ITelegramMessageRepository _messages;
    private readonly IOpenAIService _openAiService;
    private readonly ILogger<DailySummary> _logger;
    private readonly string _model;

    public DailySummary(
        DailySummaryChats chats,
        ITelegramBotClient botClient,
        ITimedTaskExecutionRepository repo,
        ILogger<DailySummary> logger,
        IWhitelist whitelist,
        ITelegramMessageRepository messages,
        SummaryOpenAiClient openAiClient) : base(repo, botClient, logger)
    {
        _chats = chats;
        _whitelist = whitelist;
        _messages = messages;
        _openAiService = openAiClient.Service;
        _model = openAiClient.Deployment;
        _logger = logger;
    }

    protected override IEnumerable<long> ChatIds => _chats.Get();
    protected override string Name => "Daily summary";
    protected override DateTime Date => DateTime.UtcNow;
    protected override string Type => "daily-summary";

    protected override async Task ExecuteForChat(long chatId)
    {
        var gptTasks = new List<Task<string>>();
        using var concurrencyLimit = new SemaphoreSlim(MaxConcurrentRequests);
        await foreach (var r in CreateGptRequestBatches(chatId, cancellation: default))
        {
            gptTasks.Add(SendGptRequestWithLimit(r, chatId, concurrencyLimit, cancellation: default));
        }

        var summaries = await Task.WhenAll(gptTasks);
        var telegramMessage = summaries.Length == 0
            ? "Сегодня ничего не произошло"
            : string.Join(Environment.NewLine, summaries);
        await _botClient.SendResponse(
            chatId: chatId,
            text: telegramMessage,
            useRich: chatId < 0 && _whitelist.IsGroupAllowedToUseBot(chatId),
            cancellationToken: default);
    }

    private async IAsyncEnumerable<ChatCompletionCreateRequest> CreateGptRequestBatches(
        long chatId,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var systemRole = _whitelist.GetSystemRoleMessage(chatId);
        await foreach (var batch in ChatMessageBuilder.CreateBatches(
            GetOrderedHistory(chatId, today, cancellation), systemRole, Prompt, cancellation: cancellation))
        {
            yield return new ChatCompletionCreateRequest { Messages = batch, Temperature = Temperature, Model = _model };
        }
    }

    private async IAsyncEnumerable<Database.TelegramMessage> GetOrderedHistory(
        long chatId,
        DateOnly today,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var messages = new List<Database.TelegramMessage>();
        await foreach (var message in _messages.Get(chatId, today, cancellation))
        {
            messages.Add(message);
        }

        foreach (var message in messages.OrderBy(x => x.Date).ThenBy(x => x.MessageId).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            yield return message;
        }
    }

    private async Task<string> SendGptRequestWithLimit(
        ChatCompletionCreateRequest request,
        long chatId,
        SemaphoreSlim concurrencyLimit,
        CancellationToken cancellation)
    {
        await concurrencyLimit.WaitAsync(cancellation);
        try
        {
            return await SendGptRequest(request, chatId, cancellation);
        }
        finally
        {
            concurrencyLimit.Release();
        }
    }

    private async Task<string> SendGptRequest(ChatCompletionCreateRequest request, long chatId, CancellationToken cancellation = default)
    {
        var completionResult = await _openAiService.ChatCompletion
            .CreateCompletion(request, _model, cancellationToken: cancellation);
        _logger.LogInformation("Daily summary tokens for chat {ChatId}: {@tokens}", chatId,
            new { completionResult.Usage?.PromptTokens, completionResult.Usage?.CompletionTokens, completionResult.Usage?.TotalTokens });
        return completionResult.Successful
            ? completionResult?.Choices[0]?.Message?.Content ?? "Successful, but no content."
            : throw new ApplicationException(completionResult?.Error?.Message ?? "Error sending open ai api request");
    }
}

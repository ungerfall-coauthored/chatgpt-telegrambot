using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OpenAI.Interfaces;
using OpenAI.ObjectModels;
using OpenAI.ObjectModels.RequestModels;
using System;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;

/// <summary>
/// Shrinks old messages using Chat GPT
/// </summary>
public class ShrinkMessage
{
    private readonly ILogger _logger;
    private readonly IOpenAIService _openAiService;
    private readonly ITelegramMessageRepository _messagesRepository;
    private readonly ITokenCounter _tokenCounter;
    private readonly AzureOpenAiSettings _azureOpenAi;
    private readonly PromptCatalog _prompts;

    public ShrinkMessage(
        IOpenAIService openAiService,
        ITokenCounter tokenCounter,
        ILogger<ShrinkMessage> logger,
        ITelegramMessageRepository messagesRepository,
        AzureOpenAiSettings azureOpenAi, PromptCatalog? prompts = null)
    {
        _openAiService = openAiService;
        _tokenCounter = tokenCounter;
        _logger = logger;
        _messagesRepository = messagesRepository;
        _azureOpenAi = azureOpenAi;
        _prompts = prompts ?? PromptCatalog.Load();
    }

    [Function("ShrinkMessage")]
    public async Task Run([TimerTrigger("0 */30 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation("C# Timer trigger function executed at: {now}", DateTime.Now);
        var minimumTokens = int.Parse(Environment.GetEnvironmentVariable("ShrinkMinimumTokens", EnvironmentVariableTarget.Process) ?? "512");
        if (minimumTokens < 1) throw new ArgumentOutOfRangeException(nameof(minimumTokens));
        await foreach (var msg in _messagesRepository.GetOldMessages(minimumTokens, CancellationToken.None))
        {
            var tokensBefore = _tokenCounter.Count(msg.Message);
            if (!await _messagesRepository.TryBeginShrink(msg, tokensBefore, minimumTokens, CancellationToken.None)) continue;
            try
            {
                var shrunkMsg = await AskChatGptToShrink(msg);
                var tokensAfter = _tokenCounter.Count(shrunkMsg);
                if (tokensAfter >= tokensBefore)
                {
                    await _messagesRepository.CompleteShrink(msg, processed: true, CancellationToken.None);
                }
                else
                {
                    await _messagesRepository.Update(new TelegramMessage
                    {
                        Id = msg.Id,
                        ChatId = msg.ChatId,
                        User = msg.User,
                        UserId = msg.UserId,
                        Message = shrunkMsg,
                        MessageId = msg.MessageId,
                        MessageThreadId = msg.MessageThreadId,
                        Date = msg.Date,
                        DateUtc = msg.DateUtc,
                        TTL = msg.TTL,
                        IsShrunk = true,
                        ShrinkProcessed = true,
                        ETag = msg.ETag,
                        OriginalMessage = msg.OriginalMessage ?? msg.Message,
                    }, CancellationToken.None);
                }
            }
            catch
            {
                try
                {
                    await _messagesRepository.CompleteShrink(msg, processed: false, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to release shrink lease for ({ChatId}, {MessageId})", msg.ChatId, msg.MessageId);
                }
                throw;
            }
        }

        _logger.LogInformation("Next timer schedule at: {nextSchedule}", timer?.ScheduleStatus?.Next);
    }

    private async Task<string> AskChatGptToShrink(TelegramMessage msg)
    {
        var completionResult = await _openAiService.ChatCompletion.CreateCompletion(
            _azureOpenAi.Chat.CreateRequest(
                [
                    ChatMessage.FromSystem(_prompts.Summaries.ShrinkSystem),
                    ChatMessage.FromUser(_prompts.Summaries.ShrinkUserPrefix + msg.Message),
                ],
                _azureOpenAi.ChatDeployment, _tokenCounter, msg.User),
            _azureOpenAi.ChatDeployment);
        _logger.LogInformation("Shrink tokens for chat {ChatId}: {@tokens}", msg.ChatId,
            new { completionResult.Usage?.PromptTokens, completionResult.Usage?.CompletionTokens, completionResult.Usage?.TotalTokens });
        var content = completionResult.Choices?.Count > 0 ? completionResult.Choices[0].Message?.Content : null;
        return completionResult.Successful && !string.IsNullOrWhiteSpace(content)
            && completionResult.Choices![0].FinishReason != "length"
            ? content
            : throw new InvalidOperationException("ChatGPT shrink request failed, was truncated, or returned no content");
    }
}

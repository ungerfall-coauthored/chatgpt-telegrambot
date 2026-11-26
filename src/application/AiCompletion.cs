using OpenAI.Chat;
using System;
using System.ClientModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ungerfall.ChatGpt.TelegramBot;

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage FromSystem(string content) => new("system", content);
    public static ChatMessage FromUser(string content) => new("user", content);
    public static ChatMessage FromAssistant(string content) => new("assistant", content);
}

public sealed record ChatCompletionRequest(
    ChatMessage[] Messages, string Model, float? Temperature, int MaxCompletionTokens, string? User);

public sealed record ChatCompletionResult(
    bool Successful, string? Content, string? FinishReason,
    int? CompletionTokens = null, int? PromptTokens = null, int? TotalTokens = null,
    string? ErrorType = null, string? ErrorMessage = null);

public interface IChatCompletionClient
{
    Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default);
}

public sealed class OpenAiChatCompletionClient(ChatClient client) : IChatCompletionClient
{
    public async Task<ChatCompletionResult> CompleteAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var messages = request.Messages.Select(message => message.Role switch
        {
            "system" => (OpenAI.Chat.ChatMessage)new SystemChatMessage(message.Content),
            "user" => new UserChatMessage(message.Content),
            "assistant" => new AssistantChatMessage(message.Content),
            _ => throw new InvalidOperationException($"Unsupported chat message role '{message.Role}'."),
        });
        var options = new ChatCompletionOptions
        {
            Temperature = request.Temperature,
            MaxOutputTokenCount = request.MaxCompletionTokens,
            EndUserId = request.User,
        };

        try
        {
            var result = await client.CompleteChatAsync(messages, options, cancellationToken);
            var completion = result.Value;
            return new ChatCompletionResult(
                true,
                string.Concat(completion.Content.Select(part => part.Text)),
                completion.FinishReason.ToString().ToLowerInvariant(),
                completion.Usage?.OutputTokenCount,
                completion.Usage?.InputTokenCount,
                completion.Usage?.TotalTokenCount);
        }
        catch (ClientResultException error)
        {
            return new ChatCompletionResult(false, null, null, ErrorType: error.GetType().Name, ErrorMessage: error.Message);
        }
    }
}

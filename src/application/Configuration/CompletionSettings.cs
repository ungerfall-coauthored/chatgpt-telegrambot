using OpenAI.ObjectModels.RequestModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public sealed class CompletionSettings
{
    public const int DefaultChatInputTokens = 16384;
    public const int DefaultSummaryInputTokens = 32768;

    public CompletionSettings(
        int contextWindowTokens = 128000, int inputTokens = DefaultChatInputTokens,
        int maxCompletionTokens = 2048, int safetyMarginTokens = 1024, float? temperature = 0f)
    {
        if (contextWindowTokens <= 0 || inputTokens <= 0 || maxCompletionTokens <= 0 || safetyMarginTokens < 0
            || (long)inputTokens + maxCompletionTokens + safetyMarginTokens > contextWindowTokens)
            throw new ArgumentException("Input tokens + max completion tokens + safety margin must fit the configured context window.");
        if (temperature.HasValue && (!float.IsFinite(temperature.Value) || temperature is < 0 or > 2))
            throw new ArgumentOutOfRangeException(nameof(temperature));
        ContextWindowTokens = contextWindowTokens;
        InputTokens = inputTokens;
        MaxCompletionTokens = maxCompletionTokens;
        SafetyMarginTokens = safetyMarginTokens;
        Temperature = temperature;
    }

    public int ContextWindowTokens { get; }
    public int InputTokens { get; }
    public int MaxCompletionTokens { get; }
    public int SafetyMarginTokens { get; }
    public float? Temperature { get; }

    public ChatCompletionCreateRequest CreateRequest(
        IList<ChatMessage> messages, string deployment, ITokenCounter? tokenCounter = null,
        string? user = null, int? maxCompletionTokens = null)
    {
        tokenCounter ??= new TokenCounter();
        var tokens = 3L + messages.Sum(m => 3L + tokenCounter.Count(m.Role) + tokenCounter.Count(m.Content ?? string.Empty));
        if (tokens > InputTokens)
            throw new InvalidOperationException("The prepared messages exceed the configured input token budget.");
        var outputTokens = maxCompletionTokens ?? MaxCompletionTokens;
        if (outputTokens <= 0 || outputTokens > MaxCompletionTokens)
            throw new ArgumentOutOfRangeException(nameof(maxCompletionTokens));
        var request = new ChatCompletionCreateRequest
        {
            Messages = messages,
            Model = deployment,
            Temperature = Temperature,
            MaxCompletionTokens = outputTokens,
        };
        if (user != null) request.User = user;
        return request;
    }

    public static CompletionSettings FromEnvironment(bool summary = false) =>
        FromConfiguration(Environment.GetEnvironmentVariable, summary);

    public static CompletionSettings FromConfiguration(Func<string, string?> getSetting, bool summary = false)
    {
        int ReadInt(string name, int fallback) =>
            getSetting(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
        var prefix = summary ? "AZURE_OPENAI_SUMMARY_" : "AZURE_OPENAI_CHAT_";
        var legacySummaryTokens = summary ? getSetting("SummaryChunkTokens") : null;
        var input = getSetting(prefix + "INPUT_TOKENS") ?? legacySummaryTokens;
        var temperature = getSetting(prefix + "TEMPERATURE");
        return new CompletionSettings(
            ReadInt(prefix + "CONTEXT_WINDOW_TOKENS", 128000),
            input == null ? summary ? DefaultSummaryInputTokens : DefaultChatInputTokens : int.Parse(input, CultureInfo.InvariantCulture),
            ReadInt(prefix + "MAX_COMPLETION_TOKENS", 2048),
            ReadInt(prefix + "SAFETY_MARGIN_TOKENS", 1024),
            string.Equals(temperature, "omit", StringComparison.OrdinalIgnoreCase) ? null
                : temperature == null ? summary ? .2f : 0f
                : float.Parse(temperature, CultureInfo.InvariantCulture));
    }

}

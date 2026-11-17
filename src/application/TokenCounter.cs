using SharpToken;
using System;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;

namespace Ungerfall.ChatGpt.TelegramBot;
public sealed class TokenCounter : ITokenCounter
{
    private static readonly GptEncoding DefaultEncoding = GptEncoding.GetEncoding("o200k_base");
    private readonly GptEncoding _encoding;

    public TokenCounter() : this(Environment.GetEnvironmentVariable("AZURE_OPENAI_TOKEN_ENCODING") ?? "o200k_base") { }

    public TokenCounter(string encodingName)
    {
        _encoding = encodingName == "o200k_base" ? DefaultEncoding : GptEncoding.GetEncoding(encodingName);
    }

    public int Count(string text)
    {
        return _encoding.CountTokens(text);
    }
}

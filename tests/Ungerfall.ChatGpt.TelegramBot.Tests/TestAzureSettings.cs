using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

internal static class TestAzureSettings
{
    public static AzureOpenAiSettings Create(CompletionSettings? chat = null, CompletionSettings? summary = null) => new(
        "https://example.openai.azure.com/", "test-key", "test-chat", "test-summary", "test-image", "2025-04-01-preview",
        chat: chat, summary: summary);
}

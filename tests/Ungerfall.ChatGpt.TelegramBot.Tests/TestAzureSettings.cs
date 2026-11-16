using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

internal static class TestAzureSettings
{
    public static AzureOpenAiSettings Create() => new(
        "https://example.openai.azure.com/", "test-key", "test-chat", "test-summary", "test-image", "2024-10-21");
}

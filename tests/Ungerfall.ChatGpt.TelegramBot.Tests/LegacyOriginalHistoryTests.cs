using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class LegacyOriginalHistoryTests
{
    [Theory]
    [InlineData(true, "Preserved original", "Friend: Preserved original")]
    [InlineData(false, "Stale original", "Friend: Current text")]
    [InlineData(true, null, "Friend: Current text")]
    public void ChatContextUsesAvailableLegacyOriginalWithoutOverridingNewText(
        bool shrunk, string? original, string expected)
    {
        var builder = ChatMessageBuilder.Create(1000).WithTokenCounter().WithSystemRoleMessage("System");
        builder.AddMessage(new TelegramMessage
        {
            User = "Friend", Message = "Current text", IsShrunk = shrunk, OriginalMessage = original,
        });
        Assert.Equal(expected, builder.Build()[1].Content);
    }

    [Fact]
    public void ChatBudgetUsesOriginalSizeNotAbbreviatedSize()
    {
        var builder = ChatMessageBuilder.Create(100).WithTokenCounter().WithSystemRoleMessage("System");
        var message = new TelegramMessage
        {
            User = "Friend", Message = "Short", IsShrunk = true,
            OriginalMessage = string.Concat(Enumerable.Repeat("original historical content ", 300)),
        };
        Assert.False(builder.CanAddMessage(message));
        Assert.Throws<InvalidOperationException>(() => builder.AddMessage(message));
    }

    [Fact]
    public async Task SummaryBatchesPreserveTheFullLegacyOriginalWhenSplitting()
    {
        var original = string.Concat(Enumerable.Repeat("original content ", 300));
        var message = new TelegramMessage
        {
            User = "Friend", Message = "Short", IsShrunk = true, OriginalMessage = original,
        };
        var parts = new List<string>();
        await foreach (var batch in ChatMessageBuilder.CreateBatches(
            new[] { message }.ToAsyncEnumerable(), "System", "Summarize", maxTokens: 100))
        {
            parts.Add(batch[1].Content["Friend: ".Length..]);
        }
        Assert.True(parts.Count > 1);
        Assert.Equal(original, string.Concat(parts));
    }
}

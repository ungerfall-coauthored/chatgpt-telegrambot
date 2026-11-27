using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class ArchiveChunkerTests
{
    [Fact]
    public void LongUnicodeOriginalRehydratesExactlyAndForwardedTextIsNotEmbedded()
    {
        var original = new HistoryMessage
        {
            ChatId = -1, SourceChatId = -1, MessageId = 1,
            Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Text = string.Concat(Enumerable.Repeat("Привет 🌍 日本語\n", 1000)), Consented = true,
        };
        original.Normalize();
        var forwarded = new HistoryMessage
        {
            ChatId = -1, SourceChatId = -1, MessageId = 2, Date = original.Date,
            Text = "Forwarded third-party original", Forwarded = true, Consented = true,
        };
        forwarded.Normalize();
        var chunks = ArchiveChunker.Create(new[] { original, forwarded },
            new HistoryWork { ChatId = -1, Id = original.Bucket, Version = 1 }, "synthetic");
        Assert.True(chunks.Count > 1);
        Assert.Equal(original.Text, string.Concat(chunks.SelectMany(chunk => chunk.Record.Starts.Zip(
            chunk.Record.Lengths, (start, length) => original.Text.Substring(start, length)))));
        var encoding = SharpToken.GptEncoding.GetEncoding("cl100k_base");
        Assert.All(chunks, chunk =>
        {
            Assert.InRange(encoding.CountTokens(chunk.Input), 1, 512);
            Assert.DoesNotContain("\uFFFD", chunk.Input);
            Assert.DoesNotContain(forwarded.Id, chunk.Record.MessageRefs);
        });
    }
}

using System.IO.Compression;
using System.Text;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class TelegramHistoryExportTests
{
    [Fact]
    public void StreamingExportPreservesFullTextAndReadsMetadataOnlyMessages()
    {
        var text = new string('x', 10000);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = 123, type = "private_supergroup",
            messages = new object[]
            {
                new { id = 1, type = "message", date_unixtime = "1704067200", from_id = "user42", text },
                new { id = -2, type = "message", date_unixtime = "1704067201", media_type = "sticker" },
                new { id = 3, type = "service", date_unixtime = "1704067202" },
            },
        });
        using var zip = Zip(json);
        using var reader = new TelegramHistoryExport(zip, 64);
        Assert.True(reader.ReadNext(out var first));
        Assert.Equal(-1000000000123, reader.ChatId);
        var message = TelegramHistoryExport.Message(first, reader.ChatId)!;
        Assert.Equal(text, message.Text);
        Assert.Equal(2024, message.YearUtc);
        Assert.Equal(42, message.UserId);
        Assert.True(reader.ReadNext(out var second));
        Assert.Equal("sticker", TelegramHistoryExport.Message(second, reader.ChatId)!.MessageType);
        Assert.Equal(-2, TelegramHistoryExport.Message(second, reader.ChatId)!.MessageId);
        Assert.True(reader.ReadNext(out var third));
        Assert.Null(TelegramHistoryExport.Message(third, reader.ChatId));
        Assert.False(reader.ReadNext(out _));
        Assert.Equal(3, reader.EntriesRead);
    }

    [Fact]
    public void OlderImportsAndReorderedUpdatesCannotOverwriteLiveEditsOrTombstones()
    {
        HistoryMessage Record(string text, string origin, int update = 0, bool deleted = false)
        {
            var result = new HistoryMessage
            {
                ChatId = -1, SourceChatId = -1, MessageId = 1, Date = DateTime.UnixEpoch,
                Text = text, Origin = origin, UpdateId = update, Deleted = deleted,
            };
            result.Normalize();
            return result;
        }
        var current = Record("new", "live", 20);
        Assert.False(HistoryRepository.Supersedes(Record("old", "export"), current));
        Assert.False(HistoryRepository.Supersedes(Record("older", "live", 19), current));
        Assert.False(HistoryRepository.Supersedes(current, current));
        Assert.False(HistoryRepository.Supersedes(current, Record("", "live", 21, true)));
        Assert.True(HistoryRepository.Supersedes(Record("newer", "live", 21), current));
    }

    private static MemoryStream Zip(string json)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("result.json").Open(), new UTF8Encoding(false));
            writer.Write(json);
        }
        stream.Position = 0;
        return stream;
    }
}

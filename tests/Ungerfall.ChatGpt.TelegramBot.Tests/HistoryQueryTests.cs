using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class HistoryQueryTests
{
    [Theory]
    [InlineData("c.chatId = -2")]
    [InlineData("c.userId = 42) OR true --")]
    [InlineData("EXISTS(SELECT VALUE 1 FROM x)")]
    [InlineData("c.text = 'x\\' OR true")]
    public void UnsafePredicatesCannotAlterServerScope(string predicate)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        { select = "VALUE COUNT(1)", predicate, groupBy = (string?)null, orderBy = (string?)null, limit = 1, cursor = (string?)null }));
        Assert.Throws<ArgumentException>(() => HistoryQuery.Create(document.RootElement, new long[] { 42 }, null));
    }

    [Fact]
    public void CompleteCountsIncludeForwardedMetadataButTextQueriesExcludeForwardedOriginals()
    {
        using var count = JsonDocument.Parse(
            """{"select":"c.yearUtc, COUNT(1) AS messages","predicate":"c.userId = 42 AND c.yearUtc >= 2015","groupBy":"c.yearUtc","orderBy":null,"limit":1,"cursor":null}""");
        var aggregate = HistoryQuery.Create(count.RootElement, new long[] { 42 }, 7);
        Assert.True(aggregate.Aggregate);
        Assert.Contains("c.threadId = @thread", aggregate.Definition.QueryText);
        Assert.DoesNotContain("TOP", aggregate.Definition.QueryText);
        Assert.DoesNotContain("c.forwarded = false", aggregate.Definition.QueryText);
        using var text = JsonDocument.Parse(
            """{"select":"c.text","predicate":"","groupBy":null,"orderBy":null,"limit":5,"cursor":null}""");
        Assert.Contains("c.forwarded = false", HistoryQuery.Create(text.RootElement, new long[] { 42 }, null).Definition.QueryText);
    }
}

using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class HistoryQueryTests
{
    [Fact]
    public void SampleEvidenceCountsOnlyReturnedAuthorTextWithoutGuessingAliases()
    {
        var method = typeof(HistoryTools).GetMethod("SampleEvidence",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var document = JsonDocument.Parse("""
            [{"userId":42,"text":"first"},{"userId":42,"text":"second"},
             {"userId":43,"text":"one"},{"userId":43,"text":""},{"text":"unknown"},{"userId":44}]
            """);
        var rows = document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
        var evidence = method.Invoke(null, new object[] { rows, "c.userId, c.text" });
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(evidence));
        var authors = result.RootElement.GetProperty("byAuthor").EnumerateArray().ToArray();
        Assert.Equal(2, authors.Length);
        Assert.Equal(42, authors[0].GetProperty("userId").GetInt64());
        Assert.Equal(2, authors[0].GetProperty("returnedMessages").GetInt32());
        Assert.Equal(43, authors[1].GetProperty("userId").GetInt64());
        Assert.Equal(1, authors[1].GetProperty("returnedMessages").GetInt32());
        Assert.Null(method.Invoke(null, new object[] { rows, "c.messageId AS userId, c.text" }));
        Assert.Null(method.Invoke(null, new object[] { rows, "c.userId" }));
    }

    [Fact]
    public void QueryRepairFeedbackNeverEchoesExceptionContents()
    {
        var feedback = typeof(HistoryTools).GetMethod("QueryFeedback",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var known = (string?)feedback.Invoke(null, new object[] { "Unknown predicate field or operator." });
        Assert.Equal("Unknown predicate field or operator. Prefix permitted fields with c.; use the documented operators.", known);
        Assert.Null(feedback.Invoke(null, new object[] { "Unknown predicate field or operator. synthetic-private-content" }));
        Assert.Null(feedback.Invoke(null, new object[] { "synthetic-private-content SELECT c.text Cosmos diagnostic" }));
    }

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

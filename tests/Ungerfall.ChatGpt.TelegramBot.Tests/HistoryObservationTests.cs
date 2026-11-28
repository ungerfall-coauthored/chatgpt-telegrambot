using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class HistoryObservationTests
{
    [Theory]
    [InlineData("Нельзя утверждать, что Алиса часто приглашает гулять.", false)]
    [InlineData("Недостаточно данных, чтобы установить её привычки.", false)]
    [InlineData("Алиса часто приглашает гулять.", true)]
    [InlineData("Он не любит спешить, часто приглашает гулять.", true)]
    [InlineData("Алиса часто приглашает, но нельзя утверждать, что Борис часто отвечает.", true)]
    public void SingleMessageFrequencyChecksAcceptCaveatsButRejectAffirmativeClaims(string answer, bool unsupported)
    {
        Assert.Equal(unsupported, DurableHistoryLiveTests.HasUnsupportedFrequencyClaim(answer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalSuccessfulEvidenceSupersedesEarlierAttempts(bool correctLast)
    {
        using var observation = new ToolCallObservationHandler();
        const string correct = "c.date >= '2022-02-20' AND c.date < '2022-03-01'";
        const string broad = "c.date >= '2022-02-01' AND c.date < '2022-03-01'";
        Add(observation, "first", correctLast ? broad : correct, true);
        Add(observation, "invalid", "date >= '2022-02-20'", false);
        Add(observation, "last", correctLast ? correct : broad, true);
        Assert.Equal(correctLast, DurableHistoryLiveTests.HasLateFebruarySources(observation));
    }

    [Fact]
    public void EvidenceMustBeReturnedToTheModel()
    {
        using var observation = new ToolCallObservationHandler();
        Add(observation, "not-returned", "c.date >= '2022-02-20' AND c.date < '2022-03-01'", true);
        observation.ToolResults.Clear();
        Assert.False(DurableHistoryLiveTests.HasLateFebruarySources(observation));
    }

    private static void Add(ToolCallObservationHandler observation, string id, string predicate, bool success)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { predicate }));
        observation.Calls.Add(new(id, "history_query", arguments.RootElement.Clone()));
        var content = success
            ? JsonSerializer.Serialize(new { status = "ok", data = new { sampleEvidence = new { byAuthor = new[] {
                new { userId = 42, returnedMessages = 1 }, new { userId = 43, returnedMessages = 1 },
            } }, rows = new[] {
                new { userId = 42, user = "Алиса", date = "2022-02-25T00:00:00Z", text = "Привет! Как здорово! Ура! Пойдём гулять в парк. fixture-alpha" },
                new { userId = 43, user = "Борис", date = "2022-02-26T00:00:00Z", text = "Считаю, что следует рассмотреть прогулку в парке. fixture-beta" },
            } } })
            : """{"status":"error","code":"invalid_query"}""";
        observation.ToolResults.Add(new(id, content));
    }
}

using System;
using System.Text.Json.Serialization;

namespace Ungerfall.ChatGpt.TelegramBot.Database;

public sealed class BookQuizProgress
{
    public string Id { get; set; } = "book-quiz-progress:" + BookQuiz.Series;
    public long ChatId { get; set; }
    public string Type { get; set; } = "book-quiz-progress";
    public int NextSequence { get; set; } = 1;
    public bool Sending { get; set; }
    public DateTime? LastSentUtc { get; set; }
    public int Ttl { get; set; } = -1;
    [JsonIgnore]
    public string? ETag { get; set; }
}
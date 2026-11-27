using System;
using System.Text.Json.Serialization;

namespace Ungerfall.ChatGpt.TelegramBot.Database;

public class TelegramMessage
{
    public const string DATE_UTC_FORMAT = "yyyy-MM-dd";

    [JsonPropertyName("id")]
    public string Id { get; init; } = null!;

    [JsonPropertyName("chatId")]
    public long ChatId { get; set; }

    [JsonPropertyName("user")]
    public string User { get; init; } = null!;

    [JsonPropertyName("userId")]
    public long UserId { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = null!;

    [JsonPropertyName("messageId")]
    public int MessageId { get; init; }

    public int? MessageThreadId { get; init; }

    [JsonPropertyName("date")]
    public DateTime Date { get; init; }

    [JsonPropertyName("dateUtc")]
    public string DateUtc { get; init; } = null!;

    [JsonPropertyName("ttl")]
    public int TTL { get; init; }

    [JsonPropertyName("isShrunk")]
    public bool IsShrunk { get; init; }

    [JsonIgnore]
    public string? ETag { get; set; }

    /// <summary>
    /// Original text retained by the retired shrink job.
    /// </summary>
    public string? OriginalMessage { get; init; }

    [JsonIgnore]
    public string EffectiveMessage => IsShrunk && !string.IsNullOrEmpty(OriginalMessage) ? OriginalMessage : Message;
}

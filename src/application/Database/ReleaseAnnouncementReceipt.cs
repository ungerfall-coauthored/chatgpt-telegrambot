using System;
using System.Text.Json.Serialization;

namespace Ungerfall.ChatGpt.TelegramBot.Database;

public sealed class ReleaseAnnouncementReceipt
{
    public const string Processing = "processing";
    public const string Sending = "sending";
    public const string Completed = "completed";

    public string Id { get; set; } = null!;
    public long ChatId { get; set; }
    public string Type { get; set; } = "release-announcement";
    public string Version { get; set; } = null!;
    public string? PreviousVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string Stage { get; set; } = Processing;
    public string LeaseId { get; set; } = null!;
    public DateTime LeaseUntilUtc { get; set; }
    public string? Summary { get; set; }
    public int? TelegramMessageId { get; set; }
    public int Ttl { get; set; } = -1;

    [JsonIgnore]
    public string? ETag { get; set; }
}
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryMessage
{
    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Kind { get; set; } = "message";
    public long SourceChatId { get; set; }
    public int MessageId { get; set; }
    public long? UserId { get; set; }
    public string User { get; set; } = "";
    public string MessageType { get; set; } = "text";
    public string Text { get; set; } = "";
    public DateTime Date { get; set; }
    public int YearUtc { get; set; }
    public DateTime? EditedAtUtc { get; set; }
    public int? ThreadId { get; set; }
    public int? ReplyToMessageId { get; set; }
    public string Origin { get; set; } = "live";
    public int UpdateId { get; set; }
    public string Revision { get; set; } = "";
    public bool Deleted { get; set; }
    public bool Consented { get; set; }
    public bool Forwarded { get; set; }
    public string Bucket { get; set; } = "";
    [JsonIgnore] public string? ETag { get; set; }

    public void Normalize()
    {
        // Preserve signed Desktop export IDs; negative IDs are not Bot API message links.
        if (ChatId >= 0 || SourceChatId >= 0 || MessageId == 0 || MessageId < 0 && Origin != "export" || Date.Kind != DateTimeKind.Utc
            || Text.Length > 1000000 || Origin is not ("live" or "export" or "legacy"))
            throw new ArgumentException("Invalid history source record.");
        Id = $"message:{SourceChatId}:{MessageId}";
        YearUtc = Date.Year;
        Bucket = $"work:{ThreadId ?? 0}:{Date:yyyyMMddHH}";
        Revision = HistoryHash.Of(System.Text.Json.JsonSerializer.Serialize(new
        {
            Text, MessageType, UserId, User, Date, EditedAtUtc, ThreadId, ReplyToMessageId, Deleted, Consented, Forwarded, UpdateId,
        }));
    }
}

public sealed class HistoryWork
{
    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Kind { get; set; } = "work";
    public long Version { get; set; }
    public long IndexedVersion { get; set; }
    public int Priority { get; set; }
    public DateTime DueUtc { get; set; }
    public DateTime? DispatchUntilUtc { get; set; }
    public string[] ActiveChunks { get; set; } = [];
    [JsonIgnore] public string? ETag { get; set; }
}

public sealed class HistoryActivity
{
    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Kind { get; set; } = "activity";
    public long? UserId { get; set; }
    public string User { get; set; } = "";
    public int YearUtc { get; set; }
    public int Messages { get; set; }
}

public sealed class HistoryConsent
{
    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Kind { get; set; } = "consent";
    public long UserId { get; set; }
    public bool Granted { get; set; }
    public DateTime ChangedUtc { get; set; }
    public bool Purging { get; set; }
}

public sealed class HistoryImport
{
    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Kind { get; set; } = "import";
    public string ArchiveHash { get; set; } = "";
    public int ProcessedEntries { get; set; }
    public int Messages { get; set; }
    public DateTime? EarliestUtc { get; set; }
    public DateTime? LatestUtc { get; set; }
    public bool Completed { get; set; }
    public double ObservedRu { get; set; }
    public string LeaseOwner { get; set; } = "";
    public DateTime LeaseUntilUtc { get; set; }
    [JsonIgnore] public string? ETag { get; set; }
}

public sealed class HistoryChunk
{
    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Bucket { get; set; } = "";
    public long Version { get; set; }
    public int? ThreadId { get; set; }
    public DateTime Date { get; set; }
    public string[] MessageRefs { get; set; } = [];
    public string[] Revisions { get; set; } = [];
    public int[] Starts { get; set; } = [];
    public int[] Lengths { get; set; } = [];
    public string Profile { get; set; } = "";
    public float[] Embedding { get; set; } = [];
}

public sealed class HistoryUsage
{
    public string Id { get; set; } = "";
    public long ChatId { get; set; }
    public string Kind { get; set; } = "usage";
    public decimal UsedUsd { get; set; }
    public Dictionary<string, decimal> Reservations { get; set; } = new();
}

public sealed record HistoryReservation(string LedgerId, string Token);

public sealed class HistorySweep
{
    public string Id { get; set; } = "maintenance:sweep";
    public long ChatId { get; set; }
    public string Kind { get; set; } = "maintenance";
    public long Cutoff { get; set; }
    public string? Continuation { get; set; }
    [JsonIgnore] public string? ETag { get; set; }
}

public static class HistoryHash
{
    public static string Of(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

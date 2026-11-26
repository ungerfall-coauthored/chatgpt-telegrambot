using System.Text.Json;

namespace Ungerfall.ChatGpt.TelegramBot.Storage;

public sealed class StorageOperationReceipt
{
    public string Id { get; init; } = "";
    public long ChatId { get; init; }
    public string Type { get; init; } = "storage-operation";
    public string Hash { get; init; } = "";
    public JsonElement Result { get; init; }
    public int Ttl { get; init; } = -1;
}
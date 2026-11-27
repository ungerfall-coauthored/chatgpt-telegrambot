using System.IO;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryCosmosSerializer : CosmosSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public override T FromStream<T>(Stream stream)
    {
        if (typeof(T) == typeof(Stream)) return (T)(object)stream;
        using (stream)
        {
            if (stream.CanSeek && stream.Length == 0) return default!;
            return JsonSerializer.Deserialize<T>(stream, Options)!;
        }
    }

    public override Stream ToStream<T>(T input) => new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(input, Options));
}

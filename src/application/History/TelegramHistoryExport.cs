using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class TelegramHistoryExport : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Stream _source;
    private byte[] _buffer;
    private int _length;
    private bool _finished;
    private JsonReaderState _state;
    private bool _messages;
    private string? _property;
    private long _id;
    private string? _type;
    public int EntriesRead { get; private set; }
    public long ChatId => _type switch
    {
        "private_supergroup" => -1000000000000 - _id,
        "private_group" => -_id,
        _ => throw new InvalidDataException("Only group history exports are supported."),
    };

    public TelegramHistoryExport(Stream zip, int bufferSize = 65536)
    {
        if (bufferSize is < 64 or > 1048576) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        _archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var entries = _archive.Entries.Where(entry => Path.GetFileName(entry.FullName) == "result.json").ToArray();
        if (entries.Length != 1 || entries[0].Length > 250L * 1024 * 1024)
        {
            _archive.Dispose();
            throw new InvalidDataException("Expected one bounded result.json entry.");
        }
        _source = entries[0].Open();
        _buffer = new byte[bufferSize];
    }

    public bool ReadNext(out JsonElement message)
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(0, _length), _finished, _state);
            var needsMore = false;
            while (true)
            {
                var previousState = reader.CurrentState;
                var previousBytes = (int)reader.BytesConsumed;
                if (!reader.Read()) break;
                if (_messages && reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 2)
                {
                    if (!JsonDocument.TryParseValue(ref reader, out var document))
                    {
                        Consume(previousBytes, previousState);
                        needsMore = true;
                        break;
                    }
                    using (document)
                    {
                        message = document!.RootElement.Clone();
                        Consume((int)reader.BytesConsumed, reader.CurrentState);
                        EntriesRead++;
                        return true;
                    }
                }
                if (reader.CurrentDepth == 1)
                {
                    if (reader.TokenType == JsonTokenType.PropertyName) _property = reader.GetString();
                    else if (_property == "id" && reader.TokenType == JsonTokenType.Number) _id = reader.GetInt64();
                    else if (_property == "type" && reader.TokenType == JsonTokenType.String) _type = reader.GetString();
                    else if (_property == "messages" && reader.TokenType == JsonTokenType.StartArray) _messages = true;
                    else if (_messages && reader.TokenType == JsonTokenType.EndArray) _messages = false;
                }
            }
            if (!needsMore) Consume((int)reader.BytesConsumed, reader.CurrentState);
            if (_finished)
            {
                if (_length != 0 || _messages || _id <= 0) throw new InvalidDataException("Incomplete history export.");
                message = default;
                return false;
            }
            if (_length == _buffer.Length)
            {
                if (_buffer.Length >= 1048576) throw new InvalidDataException("History export record exceeds its bound.");
                Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, 1048576));
            }
            var count = _source.Read(_buffer, _length, _buffer.Length - _length);
            _length += count;
            _finished = count == 0;
        }
    }

    private void Consume(int bytes, JsonReaderState state)
    {
        Buffer.BlockCopy(_buffer, bytes, _buffer, 0, _length - bytes);
        _length -= bytes;
        _state = state;
    }

    public static HistoryMessage? Message(JsonElement value, long chat)
    {
        if (!value.TryGetProperty("type", out var type) || type.GetString() != "message") return null;
        var user = value.TryGetProperty("from_id", out var fromId) ? fromId.GetString() ?? "" : "";
        var userId = user.StartsWith("user", StringComparison.Ordinal)
            && long.TryParse(user.AsSpan(4), out var parsed) ? (long?)parsed : null;
        var forwarded = value.TryGetProperty("forwarded_from", out _);
        var message = new HistoryMessage
        {
            ChatId = chat, SourceChatId = chat, MessageId = value.GetProperty("id").GetInt32(),
            UserId = userId, User = value.TryGetProperty("from", out var from) ? from.GetString() ?? "Unknown" : "Unknown",
            Text = value.TryGetProperty("text", out var text) ? Text(text) : "",
            Date = Timestamp(value, "date_unixtime"),
            EditedAtUtc = value.TryGetProperty("edited_unixtime", out _) ? Timestamp(value, "edited_unixtime") : null,
            ThreadId = value.TryGetProperty("reply_to_top_id", out var thread) ? thread.GetInt32() : null,
            ReplyToMessageId = value.TryGetProperty("reply_to_message_id", out var reply) ? reply.GetInt32() : null,
            MessageType = value.TryGetProperty("media_type", out var media) ? media.GetString() ?? "media"
                : value.TryGetProperty("photo", out _) ? "photo" : "text",
            Origin = "export", Consented = true, Forwarded = forwarded,
        };
        message.Normalize();
        return message;
    }

    private static DateTime Timestamp(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var timestamp)) throw new InvalidDataException("Export UTC timestamp is missing.");
        var seconds = timestamp.ValueKind == JsonValueKind.Number ? timestamp.GetInt64()
            : long.Parse(timestamp.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(part => part.ValueKind == JsonValueKind.String
            ? part.GetString() : part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String ? text.GetString() : "")),
        _ => "",
    };

    public void Dispose()
    {
        _source.Dispose();
        _archive.Dispose();
    }
}

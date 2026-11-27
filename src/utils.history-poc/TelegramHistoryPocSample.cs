using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed record HistoryPocChunk(long[] MessageIds, string Text, string Revision);

public static class TelegramHistoryPocSample
{
    public static IReadOnlyList<HistoryPocChunk> ReadZip(Stream source, int maximumChunks = 1000)
    {
        if (maximumChunks is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(entry => Path.GetFileName(entry.FullName) == "result.json").ToArray();
        if (entries.Length != 1 || entries[0].Length > 250L * 1024 * 1024)
            throw new InvalidDataException("Expected a single bounded result.json export.");
        using var content = entries[0].Open();
        using var json = JsonDocument.Parse(content);
        return Read(json.RootElement, maximumChunks);
    }

    public static IReadOnlyList<HistoryPocChunk> Read(JsonElement export, int maximumChunks)
    {
        if (maximumChunks is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumChunks));
        var messages = export.GetProperty("messages").EnumerateArray().Reverse();
        var chunks = new List<HistoryPocChunk>();
        var ids = new List<long>();
        var texts = new List<string>();
        string? group = null;
        var characters = 0;
        void Flush()
        {
            if (ids.Count == 0) return;
            ids.Reverse();
            texts.Reverse();
            var text = string.Join("\n", texts);
            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            chunks.Add(new(ids.ToArray(), text, revision));
            ids.Clear();
            texts.Clear();
            characters = 0;
        }
        foreach (var message in messages)
        {
            if (message.GetProperty("type").GetString() != "message") { Flush(); if (chunks.Count >= maximumChunks) break; continue; }
            var text = Text(message.GetProperty("text"));
            if (string.IsNullOrWhiteSpace(text)) continue;
            var date = message.GetProperty("date").GetString() ?? "";
            var topic = message.TryGetProperty("reply_to_top_id", out var thread) ? thread.ToString() : "";
            var nextGroup = date.Split('T')[0] + ":" + topic;
            if (group != nextGroup || ids.Count == 10 || characters + text.Length + 1 > 1500) Flush();
            if (chunks.Count >= maximumChunks) break;
            group = nextGroup;
            text = text.Length > 1500 ? text[..1500] : text;
            ids.Add(message.GetProperty("id").GetInt64());
            texts.Add(text);
            characters += text.Length + 1;
        }
        if (chunks.Count < maximumChunks) Flush();
        chunks.Reverse();
        return chunks;
    }

    private static string Text(JsonElement text) => text.ValueKind switch
    {
        JsonValueKind.String => text.GetString() ?? "",
        JsonValueKind.Array => string.Concat(text.EnumerateArray().Select(part =>
            part.ValueKind == JsonValueKind.String ? part.GetString() : part.GetProperty("text").GetString())),
        _ => "",
    };
}

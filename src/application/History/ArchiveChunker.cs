using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SharpToken;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed record ArchiveChunk(HistoryChunk Record, string Input);

public static class ArchiveChunker
{
    public static IReadOnlyList<ArchiveChunk> Create(IReadOnlyList<HistoryMessage> messages, HistoryWork work, string attempt)
    {
        var encoding = GptEncoding.GetEncoding("cl100k_base");
        var result = new List<ArchiveChunk>();
        var refs = new List<string>();
        var revisions = new List<string>();
        var starts = new List<int>();
        var lengths = new List<int>();
        var input = new StringBuilder();
        DateTime date = default;
        int? thread = null;
        void Flush()
        {
            if (refs.Count == 0) return;
            result.Add(new(new HistoryChunk
            {
                Id = $"chunk:{HistoryHash.Of(work.Id)}:{work.Version}:{attempt}:{result.Count}",
                ChatId = work.ChatId, Bucket = work.Id, Version = work.Version, Date = date, ThreadId = thread,
                MessageRefs = refs.ToArray(), Revisions = revisions.ToArray(),
                Starts = starts.ToArray(), Lengths = lengths.ToArray(), Profile = ArchiveEmbeddingClient.Profile,
            }, input.ToString()));
            input.Clear(); refs.Clear(); revisions.Clear(); starts.Clear(); lengths.Clear();
            if (result.Count > 512) throw new InvalidOperationException("Archive hour exceeds its bounded indexing capacity.");
        }
        foreach (var message in messages.OrderBy(message => message.Date).ThenBy(message => message.MessageId))
        {
            if (message.Deleted || !message.Consented || message.Forwarded || string.IsNullOrWhiteSpace(message.Text)) continue;
            var position = 0;
            while (position < message.Text.Length)
            {
                var header = $"{message.Date:O} {message.User}\n";
                if (encoding.CountTokens(header) > 128) header = $"{message.Date:O}\n";
                var low = 1;
                var high = Math.Min(message.Text.Length - position, 4096);
                var length = 0;
                while (low <= high)
                {
                    var candidate = (low + high) / 2;
                    if (position + candidate < message.Text.Length && char.IsHighSurrogate(message.Text[position + candidate - 1]))
                        candidate--;
                    if (candidate == 0) { low = 2; continue; }
                    var tokens = encoding.CountTokens(header + message.Text.Substring(position, candidate) + "\n");
                    if (tokens <= 512) { length = candidate; low = Math.Max(low + 1, candidate + 1); }
                    else high = candidate - 1;
                }
                if (length == 0) throw new InvalidOperationException("Archive text cannot be chunked without loss.");
                var piece = header + message.Text.Substring(position, length) + "\n";
                if (input.Length != 0 && encoding.CountTokens(input.ToString() + piece) > 512) Flush();
                if (refs.Count == 0) { date = message.Date; thread = message.ThreadId; }
                refs.Add(message.Id); revisions.Add(message.Revision); starts.Add(position); lengths.Add(length);
                input.Append(piece);
                position += length;
            }
        }
        Flush();
        return result;
    }
}

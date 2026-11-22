using System;
using System.Collections.Immutable;
using System.Linq;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public class BookQuizChats
{
    public virtual ImmutableHashSet<long> Get()
    {
        var value = Environment.GetEnvironmentVariable("BookQuizChats");
        return string.IsNullOrWhiteSpace(value)
            ? ImmutableHashSet<long>.Empty
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(long.Parse).ToImmutableHashSet();
    }
}
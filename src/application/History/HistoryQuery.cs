using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Cosmos;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed record HistoryQuery(QueryDefinition Definition, bool Aggregate, int Limit)
{
    public static readonly string[] Fields =
        ["id", "messageId", "userId", "user", "date", "yearUtc", "editedAtUtc", "text", "messageType", "threadId", "replyToMessageId", "origin"];

    public static HistoryQuery Create(JsonElement arguments, long[] authors, int? thread)
    {
        var select = arguments.GetProperty("select").GetString()!.Trim();
        var predicate = arguments.GetProperty("predicate").GetString()!;
        var group = arguments.GetProperty("groupBy").ValueKind == JsonValueKind.Null ? "" : arguments.GetProperty("groupBy").GetString()!.Trim();
        var order = arguments.GetProperty("orderBy").ValueKind == JsonValueKind.Null ? "" : arguments.GetProperty("orderBy").GetString()!.Trim();
        var limit = arguments.GetProperty("limit").GetInt32();
        if (limit is < 1 or > 20 || select.Length > 512 || predicate.Length > 512 || group.Length > 128 || order.Length > 128
            || predicate.Contains('\\')) throw new ArgumentException("Unsupported history query bounds.");
        var stripped = Regex.Replace(predicate, @"'([^']|'')*'", "''");
        var tokens = Regex.Matches(stripped, @"c\.[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_]+|-?\d+(?:\.\d+)?|!=|<=|>=|[=<>(),']|\s+");
        if (string.Concat(tokens.Select(token => token.Value)) != stripped) throw new ArgumentException("Unsupported predicate syntax.");
        var operators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "AND", "OR", "NOT", "TRUE", "FALSE", "NULL", "IN", "CONTAINS", "STARTSWITH", "ENDSWITH", "LOWER", "UPPER", "IS_DEFINED", "IS_NULL" };
        var allowed = Fields.Select(field => "c." + field).ToHashSet(StringComparer.Ordinal);
        var depth = 0;
        foreach (Match token in tokens)
        {
            if (token.Value == "(") depth++;
            if (token.Value == ")" && --depth < 0) throw new ArgumentException("Unbalanced predicate.");
            if (char.IsLetter(token.Value[0]) && !allowed.Contains(token.Value) && !operators.Contains(token.Value))
                throw new ArgumentException("Unknown predicate field or operator.");
        }
        if (depth != 0) throw new ArgumentException("Unbalanced predicate.");
        var scalarCount = Regex.IsMatch(select, @"^VALUE\s+COUNT\s*\(\s*1\s*\)$", RegexOptions.IgnoreCase);
        var aggregate = scalarCount;
        var projected = new List<string>();
        if (select != "*" && !scalarCount)
        {
            foreach (var part in select.Split(','))
            {
                var field = Regex.Match(part.Trim(), @"^c\.([A-Za-z][A-Za-z0-9]*)(?:\s+AS\s+[A-Za-z][A-Za-z0-9]*)?$", RegexOptions.IgnoreCase);
                var count = Regex.IsMatch(part.Trim(), @"^COUNT\s*\(\s*1\s*\)\s+AS\s+[A-Za-z][A-Za-z0-9]*$", RegexOptions.IgnoreCase);
                if (count) { aggregate = true; continue; }
                if (!field.Success || !Fields.Contains(field.Groups[1].Value, StringComparer.Ordinal))
                    throw new ArgumentException("SELECT requires permitted c.fields or COUNT(1) AS alias.");
                projected.Add("c." + field.Groups[1].Value);
            }
        }
        if (group.Length != 0)
        {
            var groups = group.Split(',').Select(value => value.Trim()).ToArray();
            if (!aggregate || scalarCount || groups.Any(value => !allowed.Contains(value))
                || projected.Any(value => !groups.Contains(value)) || groups.Any(value => !projected.Contains(value)))
                throw new ArgumentException("GROUP BY must match nonaggregate projected fields.");
        }
        else if (aggregate && projected.Count != 0) throw new ArgumentException("Mixed aggregate fields require GROUP BY.");
        if (aggregate && (order.Length != 0 || arguments.GetProperty("cursor").ValueKind != JsonValueKind.Null))
            throw new ArgumentException("Complete aggregates do not accept ORDER BY or cursors.");
        if (order.Length != 0)
        {
            var match = Regex.Match(order, @"^c\.([A-Za-z][A-Za-z0-9]*)(?:\s+(ASC|DESC))?$", RegexOptions.IgnoreCase);
            if (!match.Success || !Fields.Contains(match.Groups[1].Value, StringComparer.Ordinal))
                throw new ArgumentException("ORDER BY supports one permitted c.field.");
        }
        var bodyQuery = select == "*" || (select + predicate + group + order).Contains("c.text", StringComparison.Ordinal);
        var sql = $"SELECT {select} FROM c WHERE c.kind = 'message' AND c.deleted = false AND c.consented = true "
            + (bodyQuery ? "AND c.forwarded = false " : "")
            + "AND ARRAY_CONTAINS(@authors, c.userId)"
            + (thread == null ? "" : " AND c.threadId = @thread")
            + (string.IsNullOrWhiteSpace(predicate) ? "" : $" AND ({predicate})")
            + (group.Length == 0 ? "" : $" GROUP BY {group}")
            + (order.Length == 0 ? "" : $" ORDER BY {order}");
        return new(new QueryDefinition(sql).WithParameter("@authors", authors).WithParameter("@thread", thread), aggregate, limit);
    }
}

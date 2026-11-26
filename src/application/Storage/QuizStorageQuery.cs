using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Storage;

public sealed record QuizStorageQuery(QueryDefinition Definition, string Dataset, int Limit,
    bool FullRecords, bool Aggregate, bool Scalar, bool Exists)
{
    public static string[] Fields(string dataset) => dataset switch
    {
        "quiz_questions" => ["id", "type", "question", "options", "explanation", "correctOptionId", "posted", "deliveryPending", "version", "_ts"],
        "book_quizzes" => ["id", "type", "seriesId", "sequence", "chapter", "section", "printedPage", "pdfPage",
            "question", "options", "explanation", "correctOptionId", "imageReference", "imageDescription", "version", "_ts"],
        "book_quiz_progress" => ["id", "type", "nextSequence", "sending", "lastSentUtc", "version", "_ts"],
        _ => throw new ArgumentException("Unknown dataset."),
    };

    public static QuizStorageQuery Create(JsonElement arguments)
    {
        var dataset = arguments.TryGetProperty("dataset", out var name) ? name.GetString()! : "quiz_questions";
        var fields = Fields(dataset);
        var predicate = arguments.GetProperty("predicate").GetString()!;
        ValidatePredicate(predicate, dataset);
        var select = OptionalString(arguments, "select", "*");
        var order = OptionalString(arguments, "orderBy", "");
        var limit = arguments.GetProperty("limit").GetInt32();
        if (limit is < 1 or > 10) throw new ArgumentException("Limit must be between 1 and 10.");
        if (select.Length > 512 || order.Length > 128) throw new ArgumentException("SELECT or ORDER BY is too long.");

        var scalar = Regex.IsMatch(select, @"^VALUE\s+", RegexOptions.IgnoreCase);
        var aggregate = Regex.IsMatch(select, @"^(?:VALUE\s+)?COUNT\s*\(\s*1\s*\)(?:\s+AS\s+[A-Za-z][A-Za-z0-9_]{0,31})?$",
            RegexOptions.IgnoreCase);
        var exists = Regex.IsMatch(select, @"^VALUE\s+true$", RegexOptions.IgnoreCase);
        if (aggregate && scalar && !Regex.IsMatch(select, @"^VALUE\s+COUNT\s*\(\s*1\s*\)$", RegexOptions.IgnoreCase))
            throw new ArgumentException("VALUE COUNT(1) cannot have an alias.");
        if (!aggregate && !exists && select != "*")
        {
            var projection = scalar ? Regex.Replace(select, @"^VALUE\s+", "", RegexOptions.IgnoreCase) : select;
            var objectProjection = scalar && projection.StartsWith('{') && projection.EndsWith('}');
            var parts = (objectProjection ? projection[1..^1] : projection).Split(',');
            if (scalar && !objectProjection && parts.Length != 1) throw new ArgumentException("VALUE requires a single field or an object projection.");
            var selected = new List<string>();
            foreach (var part in parts)
            {
                var match = Regex.Match(part.Trim(), objectProjection
                    ? "^\"([A-Za-z][A-Za-z0-9_]{0,31})\"\\s*:\\s*c\\.([A-Za-z_][A-Za-z0-9_]*)$"
                    : @"^c\.([A-Za-z_][A-Za-z0-9_]*)(?:\s+AS\s+([A-Za-z][A-Za-z0-9_]{0,31}))?$",
                    RegexOptions.IgnoreCase);
                var field = match.Groups[objectProjection ? 2 : 1].Value;
                if (!match.Success || !fields.Contains(field, StringComparer.Ordinal))
                    throw new ArgumentException("SELECT supports *, permitted c.fields, VALUE c.field/object, VALUE true, and VALUE COUNT(1). FROM and partition are server-owned.");
                if (scalar && !objectProjection && match.Groups[2].Success)
                    throw new ArgumentException("VALUE cannot have an alias.");
                var sql = field == "version" ? "c._etag" : "c." + field;
                if (objectProjection) selected.Add(JsonSerializer.Serialize(match.Groups[1].Value) + ": " + sql);
                else if (!scalar && (match.Groups[2].Success || field == "version"))
                    selected.Add(sql + " AS " + (match.Groups[2].Success ? match.Groups[2].Value : field));
                else selected.Add(sql);
            }
            select = objectProjection ? "VALUE {" + string.Join(", ", selected) + "}"
                : (scalar ? "VALUE " : "") + string.Join(", ", selected);
        }
        if (aggregate && order.Length != 0) throw new ArgumentException("COUNT does not support ORDER BY; restrict the subset with WHERE.");
        if (order.Length != 0)
        {
            foreach (var part in order.Split(','))
            {
                var match = Regex.Match(part.Trim(), @"^c\.([A-Za-z_][A-Za-z0-9_]*)(?:\s+(?:ASC|DESC))?$", RegexOptions.IgnoreCase);
                if (!match.Success || match.Groups[1].Value == "version" || !fields.Contains(match.Groups[1].Value, StringComparer.Ordinal))
                    throw new ArgumentException("ORDER BY supports only permitted c.fields with ASC or DESC.");
            }
        }

        var filter = dataset switch
        {
            "quiz_questions" => "c.type IN (@cs, @films, @games)",
            "book_quizzes" => "c.type = @book AND c.seriesId = @series",
            _ => "c.type = @progress AND c.id = @progressId",
        };
        var top = aggregate ? "" : $"TOP {(exists ? 1 : limit + 1)} ";
        var query = new QueryDefinition($"SELECT {top}{select} FROM c WHERE {filter}"
            + (string.IsNullOrWhiteSpace(predicate) ? "" : $" AND ({predicate})")
            + (order.Length == 0 ? "" : $" ORDER BY {order}"))
            .WithParameter("@cs", TimedTaskQuiz.Type_ComputerScience)
            .WithParameter("@films", TimedTaskQuiz.Type_Films)
            .WithParameter("@games", TimedTaskQuiz.Type_VideoGames)
            .WithParameter("@book", "book-quiz").WithParameter("@series", BookQuiz.Series)
            .WithParameter("@progress", "book-quiz-progress").WithParameter("@progressId", "book-quiz-progress:" + BookQuiz.Series);
        return new(query, dataset, limit, select == "*", aggregate, scalar, exists);
    }

    public static void ValidatePredicate(string predicate, string dataset = "quiz_questions")
    {
        if (predicate == null || predicate.Length > 512) throw new ArgumentException("Predicate must be at most 512 characters.");
        if (predicate.Contains('\\')) throw new ArgumentException("Backslash escapes are not supported in predicates.");
        var stripped = Regex.Replace(predicate, @"'([^']|'')*'", "''");
        var tokens = Regex.Matches(stripped,
            @"c\.[A-Za-z_][A-Za-z0-9_]*|[A-Za-z_]+|\d+(?:\.\d+)?|!=|<=|>=|[=<>(),']|\s+", RegexOptions.CultureInvariant);
        if (string.Concat(tokens.Select(t => t.Value)) != stripped)
            throw new ArgumentException("Unsupported predicate syntax. Supply a WHERE expression, not a full query.");
        var allowed = new HashSet<string>(Fields(dataset).Where(field => field != "version").Select(field => "c." + field), StringComparer.Ordinal);
        var operators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "AND", "OR", "NOT", "TRUE", "FALSE", "NULL", "CONTAINS", "STARTSWITH", "ENDSWITH", "LOWER", "UPPER", "IS_DEFINED", "IS_NULL" };
        var depth = 0;
        foreach (Match token in tokens)
        {
            if (token.Value == "(") depth++;
            if (token.Value == ")" && --depth < 0) throw new ArgumentException("Unbalanced predicate parentheses.");
            if (char.IsLetter(token.Value[0]) && !allowed.Contains(token.Value) && !operators.Contains(token.Value))
                throw new ArgumentException("Unknown field or operator. Use the dataset's storage_schema.");
        }
        if (depth != 0) throw new ArgumentException("Unbalanced predicate parentheses.");
    }

    private static string OptionalString(JsonElement arguments, string name, string fallback) =>
        !arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? fallback : value.GetString()!.Trim();
}
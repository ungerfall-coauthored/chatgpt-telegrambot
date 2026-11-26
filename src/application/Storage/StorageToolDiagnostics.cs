using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ungerfall.ChatGpt.TelegramBot.Storage;

internal static class StorageToolDiagnostics
{
    private static readonly HashSet<string> Words = new(
        "SELECT FROM WHERE VALUE COUNT AS TOP ORDER BY ASC DESC AND OR NOT TRUE FALSE NULL IN CONTAINS STARTSWITH ENDSWITH LOWER UPPER IS_DEFINED IS_NULL"
            .Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Fields = new(
        new[] { "quiz_questions", "book_quizzes", "book_quiz_progress" }
            .SelectMany(QuizStorageQuery.Fields).Select(field => "c." + field), StringComparer.Ordinal);

    public static string Name(string name) =>
        StorageTools.Definitions.Any(tool => tool.FunctionName == name) ? name : "unknown";

    public static string Describe(string arguments)
    {
        if (arguments.Length > 16000) return "oversized_arguments";
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "invalid_arguments";
            var dataset = root.TryGetProperty("dataset", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is "quiz_questions" or "book_quizzes" or "book_quiz_progress"
                ? value.GetString() : "unknown";
            var limit = root.TryGetProperty("limit", out value) && value.TryGetInt32(out var number) && number is >= 1 and <= 64
                ? number : (int?)null;
            return JsonSerializer.Serialize(new
            {
                dataset,
                limit,
                select = Clause(root, "select"),
                predicate = Clause(root, "predicate"),
                orderBy = Clause(root, "orderBy"),
            });
        }
        catch (JsonException) { return "invalid_json"; }
        catch (InvalidOperationException) { return "invalid_arguments"; }
    }

    public static object? ScalarResult(string arguments, StorageResult result)
    {
        if (result.Status != "ok" || arguments.Length > 16000) return null;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (!document.RootElement.TryGetProperty("select", out var select) || select.ValueKind != JsonValueKind.String) return null;
            var data = JsonSerializer.SerializeToElement(result.Data, StorageResult.JsonOptions);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("value", out var value)) return null;
            if (select.GetString()!.Trim().Equals("VALUE true", StringComparison.OrdinalIgnoreCase)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            return select.GetString()!.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? Clause(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Shape(value.GetString()!) : null;

    private static string Shape(string text)
    {
        var result = new StringBuilder();
        for (var index = 0; index < text.Length && result.Length < 1000;)
        {
            var character = text[index++];
            if (character is '\'' or '"')
            {
                while (index < text.Length)
                {
                    var next = text[index++];
                    if (next == '\\') { if (index < text.Length) index++; }
                    else if (next == character)
                    {
                        if (index < text.Length && text[index] == character) index++;
                        else break;
                    }
                }
                result.Append("<literal>");
            }
            else if (char.IsAsciiLetter(character) || character == '_')
            {
                var start = index - 1;
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] is '_' or '.')) index++;
                var word = text[start..index];
                result.Append(Fields.Contains(word) || Words.Contains(word) ? word : "<identifier>");
            }
            else if (char.IsDigit(character))
            {
                while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.')) index++;
                result.Append("<number>");
            }
            else result.Append("=<>!(),.*+-".Contains(character) ? character : ' ');
        }
        return result.ToString();
    }
}

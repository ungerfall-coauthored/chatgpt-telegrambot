using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace Ungerfall.ChatGpt.TelegramBot.Storage;

public sealed record StorageContext(long ChatId, long UserId, int MessageId, string CurrentRequest);

public sealed record StorageResult(string Status, string? Code = null, string? Message = null, object? Data = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}

public interface IQuizStorage
{
    Task<StorageResult> ExecuteAsync(string operation, JsonElement arguments, StorageContext context,
        int mutationIndex, CancellationToken cancellation);
}

public sealed class StorageTools(IQuizStorage repository)
{
    public const long AdministratorUserId = 66215515;
    public static bool IsAuthorized(StorageContext? context) => context is { UserId: AdministratorUserId, MessageId: > 0 };

    public static readonly IReadOnlyList<ChatTool> Definitions =
    [
        Define("storage_catalog", "Discover registered datasets and permitted operations. Container selection is server-owned.",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}"""),
        Define("storage_schema", "Get the quiz record schema and Cosmos SQL predicate rules.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]}},"required":["dataset"],"additionalProperties":false}"""),
        Define("storage_list",         "Read at most 10 quiz records in the current chat. Supply a Cosmos SQL WHERE predicate, not KQL or a full SELECT. No pagination. Use c.question, c.type, c.explanation, c.correctOptionId, c.posted. Results include IDs and versions.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]},"predicate":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["dataset","predicate","limit"],"additionalProperties":false}"""),
        Define("storage_get", "Read one quiz record in the current chat, including its version.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]},"id":{"type":"string"}},"required":["dataset","id"],"additionalProperties":false}"""),
        Define("storage_create", "Create 1-5 quiz questions atomically. Use only for an explicit request from the current user; discover schema first. IDs and chat are assigned by the server.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]},"records":{"type":"array","minItems":1,"maxItems":5,"items":{"type":"object","properties":{"type":{"type":"string","enum":["quiz-type-cs","quiz-type-films"," quiz-type-videogames"]},"question":{"type":"string"},"options":{"type":"array","items":{"type":"string"}},"correctOptionId":{"type":"integer"},"explanation":{"type":"string"}},"required":["type","question","options","correctOptionId","explanation"],"additionalProperties":false}}},"required":["dataset","records"],"additionalProperties":false}"""),
        Define("storage_update",         "Replace a quiz's editable fields using its exact version from get/list. Posted quizzes and those pending scheduled delivery are immutable. Requires an explicit current-user request.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]},"id":{"type":"string"},"version":{"type":"string"},"record":{"type":"object","properties":{"type":{"type":"string","enum":["quiz-type-cs","quiz-type-films"," quiz-type-videogames"]},"question":{"type":"string"},"options":{"type":"array","items":{"type":"string"}},"correctOptionId":{"type":"integer"},"explanation":{"type":"string"}},"required":["type","question","options","correctOptionId","explanation"],"additionalProperties":false}},"required":["dataset","id","version","record"],"additionalProperties":false}"""),
        Define("storage_delete",         "Delete one unposted quiz not pending scheduled delivery using its exact version. Only execute when the current user explicitly requests deletion of that record; ask for clarification when ambiguous.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]},"id":{"type":"string"},"version":{"type":"string"}},"required":["dataset","id","version"],"additionalProperties":false}"""),
    ];

    public async Task<StorageResult> ExecuteAsync(string name, string json, StorageContext context,
        int mutationIndex, CancellationToken cancellation)
    {
        if (!IsAuthorized(context)) return new("error", "forbidden", "Storage tools are restricted to the administrator.");
        if (json.Length > 16000) return new("error", "validation_error", "Tool arguments exceed 16000 characters.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var arguments = document.RootElement;
            var fields = name switch
            {
                "storage_catalog" => Array.Empty<string>(),
                "storage_schema" => ["dataset"],
                "storage_list" => ["dataset", "predicate", "limit"],
                "storage_get" => ["dataset", "id"],
                "storage_create" => ["dataset", "records"],
                "storage_update" => ["dataset", "id", "version", "record"],
                "storage_delete" => ["dataset", "id", "version"],
                _ => throw new ArgumentException("Unknown storage tool."),
            };
            RequireFields(arguments, fields);
            if (name == "storage_catalog")
                return new("ok", Data: new
                {
                    datasets = new[] { new { name = "quiz_questions", description = "Existing daily computer-science, film and video-game quizzes in this chat.",
                        operations = new[] { "list", "get", "create", "update", "delete" } } },
                });
            if (arguments.GetProperty("dataset").GetString() != "quiz_questions")
                return new("error", "unknown_dataset", "Registered dataset: quiz_questions.");
            if (name == "storage_schema")
                return new("ok", Data: new
                {
                    fields = new
                    {
                        type = new[] { "quiz-type-cs", "quiz-type-films", " quiz-type-videogames" },
                        question = "1-300 characters",
                        options = "2-10 distinct choices, each 1-100 characters",
                        correctOptionId = "zero-based index",
                        explanation = "0-200 characters"
                    },
                    predicates = "Cosmos SQL WHERE expression only, without backslash escapes. Fields: c.question, c.type, c.explanation, c.correctOptionId, c.posted. Operators: = != < <= > >= AND OR NOT. Functions: CONTAINS, STARTSWITH, ENDSWITH, LOWER, UPPER, IS_DEFINED, IS_NULL. Example: CONTAINS(LOWER(c.question), 'linux'). Empty predicate lists up to limit records.",
                    permissions = "Only current chat, administrator only. Cannot edit/delete posted quizzes, quizzes pending delivery, or internal state. Use versions for update/delete.",
                });
            return await repository.ExecuteAsync(name, arguments, context, mutationIndex, cancellation);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return new("error", "validation_error", "Invalid tool arguments. Follow storage_schema and the function's JSON schema.");
        }
    }

    internal static void RequireFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != fields.Length
            || value.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal))
            || fields.Any(field => !value.TryGetProperty(field, out _)))
            throw new ArgumentException("Invalid argument fields.");
    }

    private static ChatTool Define(string name, string description, string schema) =>
        ChatTool.CreateFunctionTool(name, description, BinaryData.FromString(schema), functionSchemaIsStrict: true);
}
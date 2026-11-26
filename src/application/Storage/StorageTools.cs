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
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions","book_quizzes","book_quiz_progress"]}},"required":["dataset"],"additionalProperties":false}"""),
        Define("storage_list", "Query current-chat storage with a SELECT clause, WHERE predicate and ORDER BY clause; FROM and partition are server-owned. Prefer VALUE COUNT(1) for counts, VALUE true with limit 1 for existence, or small projections such as c.id, c.correctOptionId. c.version is a SELECT-only version token. Use * only for records you need to edit. Fields: id, type, question, options, explanation, correctOptionId, _ts; quiz_questions adds posted, deliveryPending; book_quizzes adds seriesId, sequence, chapter, section, printedPage, pdfPage, imageReference, imageDescription; book_quiz_progress has only id, type, nextSequence, sending, lastSentUtc, _ts. No pagination; at most 10 rows. COUNT is complete or fails, never approximate; VALUE true never returns an unproven false.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions","book_quizzes","book_quiz_progress"]},"select":{"type":["string","null"]},"predicate":{"type":"string"},"orderBy":{"type":["string","null"]},"limit":{"type":"integer","minimum":1,"maximum":10}},"required":["dataset","select","predicate","orderBy","limit"],"additionalProperties":false}"""),
        Define("storage_get", "Read one quiz record in the current chat, including its version.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions","book_quizzes","book_quiz_progress"]},"id":{"type":"string"}},"required":["dataset","id"],"additionalProperties":false}"""),
        Define("storage_create", "Create 1-5 quiz questions atomically. Use only for an explicit request from the current user; discover schema first. IDs and chat are assigned by the server.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]},"records":{"type":"array","minItems":1,"maxItems":5,"items":{"type":"object","properties":{"type":{"type":"string","enum":["quiz-type-cs","quiz-type-films"," quiz-type-videogames"]},"question":{"type":"string"},"options":{"type":"array","items":{"type":"string"}},"correctOptionId":{"type":"integer"},"explanation":{"type":"string"}},"required":["type","question","options","correctOptionId","explanation"],"additionalProperties":false}}},"required":["dataset","records"],"additionalProperties":false}"""),
        Define("storage_update", "Replace a quiz's editable fields using its exact version from get/list. For book quizzes preserve citation/sequence metadata; delivered quizzes and in-flight series are immutable. When reshuffling options preserve the correct answer text and adjust its zero-based index. Requires an explicit current-user request.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions","book_quizzes"]},"id":{"type":"string"},"version":{"type":"string"},"record":{"type":"object","properties":{"type":{"type":"string","enum":["quiz-type-cs","quiz-type-films"," quiz-type-videogames","book-quiz"]},"question":{"type":"string"},"options":{"type":"array","items":{"type":"string"}},"correctOptionId":{"type":"integer"},"explanation":{"type":"string"}},"required":["type","question","options","correctOptionId","explanation"],"additionalProperties":false}},"required":["dataset","id","version","record"],"additionalProperties":false}"""),
        Define("storage_delete",         "Delete one unposted quiz not pending scheduled delivery using its exact version. Only execute when the current user explicitly requests deletion of that record; ask for clarification when ambiguous.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["quiz_questions"]},"id":{"type":"string"},"version":{"type":"string"}},"required":["dataset","id","version"],"additionalProperties":false}"""),
        Define("storage_shuffle_options", "Shuffle only answer options of upcoming Linux book questions, without changing question text, sequence, citation or the correct answer text. Use only when the current user explicitly asks to reshuffle options/the Linux quiz. Processes up to limit questions per batch and returns only count, IDs and hasMore. Repeat while hasMore is true; already-processed questions are skipped for this request. Delivered questions and in-flight delivery are protected.",
            """{"type":"object","properties":{"dataset":{"type":"string","enum":["book_quizzes"]},"limit":{"type":"integer","minimum":1,"maximum":5}},"required":["dataset","limit"],"additionalProperties":false}"""),
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
                "storage_shuffle_options" => ["dataset", "limit"],
                _ => throw new ArgumentException("Unknown storage tool."),
            };
            if (name == "storage_list") RequireListFields(arguments);
            else RequireFields(arguments, fields);
            if (name == "storage_catalog")
                return new("ok", Data: new
                {
                    datasets = new[] { new { name = "quiz_questions", description = "Existing daily computer-science, film and video-game quizzes in this chat.",
                        operations = new[] { "list", "get", "create", "update", "delete" } },
                        new { name = "book_quizzes", description = "The Linux Programming Interface book quizzes in this chat; delivered records are immutable.",
                            operations = new[] { "list", "get", "update", "shuffle_options" } },
                        new { name = "book_quiz_progress", description = "Read-only book delivery progress: nextSequence is the first not-yet-delivered question.",
                            operations = new[] { "list", "get" } } },
                });
            var dataset = arguments.GetProperty("dataset").GetString();
            if (dataset is not ("quiz_questions" or "book_quizzes" or "book_quiz_progress"))
                return new("error", "unknown_dataset", "Use a dataset from storage_catalog.");
            if ((dataset == "book_quiz_progress" && name is not ("storage_list" or "storage_get" or "storage_schema"))
                || (dataset == "book_quizzes" && name is ("storage_create" or "storage_delete"))
                || (name == "storage_shuffle_options" && dataset != "book_quizzes"))
                return new("error", "forbidden", "Book progress is read-only. Book quiz creation/deletion uses ordered ingestion; tools may update only upcoming quizzes.");
            if (name == "storage_schema")
                return new("ok", Data: new
                {
                    fields = new
                    {
                        type = dataset switch
                        {
                            "book_quizzes" => new[] { "book-quiz" },
                            "book_quiz_progress" => new[] { "book-quiz-progress" },
                            _ => new[] { "quiz-type-cs", "quiz-type-films", " quiz-type-videogames" },
                        },
                        question = "1-300 characters",
                        options = dataset == "book_quizzes" ? "4-8 distinct choices, each 1-100 characters" : "2-10 distinct choices, each 1-100 characters",
                        correctOptionId = "zero-based index",
                        explanation = "0-200 characters"
                    },
                    queryFields = QuizStorageQuery.Fields(dataset),
                    select = "SELECT clause only: *, c.id, c.options, c.version, VALUE c.field, VALUE {\"id\":c.id,\"version\":c.version}, VALUE true, VALUE COUNT(1), or COUNT(1) AS count. version is SELECT-only, not a stored field. No FROM, joins, subqueries, arbitrary functions or internal fields. COUNT ignores limit and requires orderBy null. VALUE COUNT/true produce scalar data.value; other VALUE projections produce an array in data.value. Regular projections are data.records. Fetch full records with storage_get when editing.",
                    orderBy = "ORDER BY clause only, e.g. c.sequence DESC for book quizzes; c._ts DESC is last modification time for ordinary quizzes.",
                    predicates = "Cosmos SQL WHERE expression only, without backslash escapes. Operators: = != < <= > >= AND OR NOT. Functions: CONTAINS, STARTSWITH, ENDSWITH, LOWER, UPPER, IS_DEFINED, IS_NULL. Field names are case-sensitive.",
                    bookDelivery = "Read book_quiz_progress first; absent progress means nextSequence=1 and sending=false. Delivered book sequences are less than nextSequence; the last N delivered are max(1,nextSequence-N) through nextSequence-1. Use that range with VALUE COUNT(1) and c.correctOptionId = 0 to count answer position 1. Upcoming quizzes have sequence >= nextSequence. For options-only reshuffling use storage_shuffle_options until hasMore is false, not record rewrites. Progress and book metadata (seriesId, sequence, chapter, section, pages, images) are read-only.",
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

    private static void RequireListFields(JsonElement value)
    {
        string[] allowed = ["dataset", "select", "predicate", "orderBy", "limit"];
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("dataset", out _)
            || !value.TryGetProperty("predicate", out _) || !value.TryGetProperty("limit", out _)
            || value.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal))
            || value.EnumerateObject().Select(property => property.Name).Distinct().Count() != value.EnumerateObject().Count())
            throw new ArgumentException("Invalid list argument fields.");
    }

    private static ChatTool Define(string name, string description, string schema) =>
        ChatTool.CreateFunctionTool(name, description, BinaryData.FromString(schema), functionSchemaIsStrict: true);
}
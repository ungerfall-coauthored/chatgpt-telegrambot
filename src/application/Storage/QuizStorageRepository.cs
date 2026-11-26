using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Storage;

public sealed class QuizStorageRepository(CosmosClient cosmos, IOptions<CosmosDbOptions> options,
    ILogger<QuizStorageRepository> logger) : IQuizStorage
{
    private Container Container => cosmos.GetContainer(options.Value.DatabaseId, options.Value.TimedTasksContainerId);
    private static readonly string[] QuizTypes =
        [TimedTaskQuiz.Type_ComputerScience, TimedTaskQuiz.Type_Films, TimedTaskQuiz.Type_VideoGames];

    public async Task<StorageResult> ExecuteAsync(string operation, JsonElement arguments, StorageContext context,
        int mutationIndex, CancellationToken cancellation)
    {
        if (!StorageTools.IsAuthorized(context)) return new("error", "forbidden", "Storage access denied.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (operation == "storage_list") return await List(arguments, context.ChatId, timeout.Token);
            if (operation == "storage_get")
            {
                var response = await Read(arguments.GetProperty("id").GetString()!, context.ChatId, timeout.Token);
                var view = View(response.Resource, response.ETag);
                return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(view, StorageResult.JsonOptions)) > 12000
                    ? new("error", "budget_exceeded", "Quiz exceeds the 12000-byte tool result limit.")
                    : new("ok", Data: view);
            }
            return await Mutate(operation, arguments, context, mutationIndex, timeout.Token);
        }
        catch (CosmosException ex)
        {
            var code = ex.StatusCode switch
            {
                HttpStatusCode.BadRequest => "invalid_query",
                HttpStatusCode.NotFound => "not_found",
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => "conflict",
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout => "temporarily_unavailable",
                _ => "storage_error",
            };
            logger.LogWarning("Storage operation {Operation} failed: HTTP {Status}, activity {ActivityId}",
                operation, (int)ex.StatusCode, ex.ActivityId);
            return new("error", code, code == "invalid_query"
                ? "Invalid Cosmos SQL predicate. Check field names, operators, quoting and parentheses using storage_schema."
                : "Storage operation did not complete successfully. For a conflict, read the current record before retrying.");
        }
        catch (ArgumentException ex)
        {
            return new("error", operation == "storage_list" ? "invalid_query" : "validation_error", ex.Message);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new("error", "temporarily_unavailable", "Storage operation exceeded its 15-second timeout.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            logger.LogError("Storage operation {Operation} failed ({ExceptionType}); correlation {CorrelationId}",
                operation, ex.GetType().Name, correlationId);
            return new("error", "internal_error", $"Storage operation failed; correlation ID: {correlationId}.");
        }
    }

    private async Task<StorageResult> List(JsonElement arguments, long chatId, CancellationToken cancellation)
    {
        var predicate = arguments.GetProperty("predicate").GetString()!;
        ValidatePredicate(predicate);
        var limit = arguments.GetProperty("limit").GetInt32();
        if (limit is < 1 or > 10) throw new ArgumentException("Limit must be between 1 and 10.");
        var query = new QueryDefinition($"SELECT TOP {limit + 1} * FROM c WHERE c.type IN (@cs, @films, @games)"
            + (string.IsNullOrWhiteSpace(predicate) ? "" : $" AND ({predicate})"))
            .WithParameter("@cs", QuizTypes[0]).WithParameter("@films", QuizTypes[1]).WithParameter("@games", QuizTypes[2]);
        using var iterator = Container.GetItemQueryIterator<JsonElement>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(chatId),
                MaxItemCount = limit + 1,
                MaxConcurrency = 1,
                MaxBufferedItemCount = limit + 1,
                EnableScanInQuery = false
            });
        var records = new List<object>();
        double charge = 0;
        var truncated = false;
        var bytes = 0;
        // At most two pages, including empty pages. Never drain an unbounded iterator.
        for (var page = 0; page < 2 && iterator.HasMoreResults; page++)
        {
            var response = await iterator.ReadNextAsync(cancellation);
            charge += response.RequestCharge;
            if (charge > 50) return new("error", "budget_exceeded", "Query exceeded the 50 RU read budget. Narrow the predicate.");
            foreach (var record in response)
            {
                if (records.Count == limit) { truncated = true; break; }
                var view = View(record.Deserialize<TimedTaskQuiz>()!, record.GetProperty("_etag").GetString()!);
                var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(view, StorageResult.JsonOptions));
                if (bytes + size > 12000) { truncated = true; break; }
                records.Add(view);
                bytes += size;
            }
            if (truncated || records.Count == limit) break;
        }
        return new("ok", Data: new { records, truncated = truncated || iterator.HasMoreResults, requestCharge = charge });
    }

    public static void ValidatePredicate(string predicate)
    {
        if (predicate == null || predicate.Length > 512)
            throw new ArgumentException("Predicate must be at most 512 characters.");
        var stripped = Regex.Replace(predicate, @"'([^']|'')*'", "''");
        var tokens = Regex.Matches(stripped,
            @"c\.[A-Za-z]+|[A-Za-z_]+|\d+(?:\.\d+)?|!=|<=|>=|[=<>(),']|\s+", RegexOptions.CultureInvariant);
        if (string.Concat(tokens.Select(t => t.Value)) != stripped)
            throw new ArgumentException("Unsupported predicate syntax. Use a Cosmos SQL WHERE expression, not a full query.");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "c.question", "c.type", "c.explanation", "c.correctOptionId", "c.posted",
            "AND", "OR", "NOT", "TRUE", "FALSE", "NULL",
            "CONTAINS", "STARTSWITH", "ENDSWITH", "LOWER", "UPPER", "IS_DEFINED", "IS_NULL",
        };
        var depth = 0;
        foreach (Match token in tokens)
        {
            if (token.Value == "(") depth++;
            if (token.Value == ")" && --depth < 0) throw new ArgumentException("Unbalanced predicate parentheses.");
            if (char.IsLetter(token.Value[0]) && !allowed.Contains(token.Value))
                throw new ArgumentException("Unknown field or operator. Allowed fields: c.question, c.type, c.explanation, c.correctOptionId, c.posted.");
        }
        if (depth != 0) throw new ArgumentException("Unbalanced predicate parentheses.");
    }

    private async Task<ItemResponse<TimedTaskQuiz>> Read(string id, long chatId, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new ArgumentException("Invalid quiz ID.");
        var response = await Container.ReadItemAsync<TimedTaskQuiz>(id, new PartitionKey(chatId), cancellationToken: cancellation);
        if (response.Resource.ChatId != chatId || !QuizTypes.Contains(response.Resource.Type))
            throw new ArgumentException("Record is not a supported quiz in this chat.");
        return response;
    }

    private async Task<StorageResult> Mutate(string operation, JsonElement arguments, StorageContext context,
        int mutationIndex, CancellationToken cancellation)
    {
        if (mutationIndex is < 1 or > 12) throw new ArgumentException("Invalid mutation operation index.");
        var receiptId = $"storage-operation:{context.UserId}:{context.MessageId}:{mutationIndex}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + arguments.GetRawText())));
        var partition = new PartitionKey(context.ChatId);
        try
        {
            var receipt = (await Container.ReadItemAsync<JsonElement>(receiptId, partition, cancellationToken: cancellation)).Resource;
            return receipt.GetProperty("hash").GetString() == hash
                ? JsonSerializer.Deserialize<StorageResult>(receipt.GetProperty("result").GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
                : new("error", "conflict", "This operation slot already completed with different arguments. Do not repeat the write.");
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }

        var batch = Container.CreateTransactionalBatch(partition);
        var ids = new List<string>();
        if (operation == "storage_create")
        {
            var records = arguments.GetProperty("records");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() is < 1 or > 5)
                throw new ArgumentException("Create requires 1-5 quiz records.");
            var index = 0;
            foreach (var record in records.EnumerateArray())
            {
                var id = $"tool-quiz:{context.UserId}:{context.MessageId}:{mutationIndex}:{index++}";
                batch.CreateItem(ParseRecord(record, id, context.ChatId));
                ids.Add(id);
            }
        }
        else if (operation is "storage_update" or "storage_delete")
        {
            var id = arguments.GetProperty("id").GetString()!;
            var current = await Read(id, context.ChatId, cancellation);
            var version = arguments.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(version) || current.ETag.Trim('"') != version.Trim('"'))
                return new("error", "conflict", "Version changed. Read the current record.");
            if (current.Resource.Posted) return new("error", "forbidden", "Already posted quizzes cannot be changed.");
            var requestOptions = new TransactionalBatchItemRequestOptions { IfMatchEtag = current.ETag };
            if (operation == "storage_delete") batch.DeleteItem(id, requestOptions);
            else batch.ReplaceItem(id, ParseRecord(arguments.GetProperty("record"), id, context.ChatId), requestOptions);
            ids.Add(id);
        }
        else throw new ArgumentException("Unknown storage operation.");

        var result = new StorageResult("ok", Data: new { operation, count = ids.Count, ids });
        // Receipt and mutation commit atomically in the existing /chatId partition.
        batch.CreateItem(new StorageOperationReceipt
        {
            Id = receiptId,
            ChatId = context.ChatId,
            Hash = hash,
            Result = JsonSerializer.SerializeToElement(result, StorageResult.JsonOptions)
        });
        using var response = await batch.ExecuteAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            return new("error", response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                ? "conflict" : "storage_error", "Atomic write failed; no records were changed. Retry the same operation or read the latest version.");
        return result;
    }

    private static TimedTaskQuiz ParseRecord(JsonElement record, string id, long chatId)
    {
        StorageTools.RequireFields(record, "type", "question", "options", "correctOptionId", "explanation");
        var quiz = record.Deserialize<TimedTaskQuizInput>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Invalid quiz record.");
        if (!QuizTypes.Contains(quiz.Type) || string.IsNullOrWhiteSpace(quiz.Question) || quiz.Question.Length > 300
            || quiz.Options == null || quiz.Options.Length is < 2 or > 10
            || quiz.Options.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 100)
            || quiz.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != quiz.Options.Length
            || quiz.CorrectOptionId < 0 || quiz.CorrectOptionId >= quiz.Options.Length
            || quiz.Explanation == null || quiz.Explanation.Length > 200 || quiz.Explanation.Count(c => c == '\n') > 2)
            throw new ArgumentException("Invalid quiz: supported type, question 1-300 chars, 2-10 distinct options of 1-100 chars, valid answer index, explanation 0-200 chars and at most 2 newlines required.");
        return new TimedTaskQuiz
        {
            Id = id,
            ChatId = chatId,
            Type = quiz.Type,
            Question = quiz.Question,
            Options = quiz.Options,
            CorrectOptionId = quiz.CorrectOptionId,
            Explanation = quiz.Explanation
        };
    }

    private static object View(TimedTaskQuiz quiz, string version) => new
    {
        id = quiz.Id,
        type = quiz.Type,
        question = quiz.Question,
        options = quiz.Options,
        correctOptionId = quiz.CorrectOptionId,
        explanation = quiz.Explanation,
        posted = quiz.Posted,
        version = version.Trim('"'),
    };

    private sealed record TimedTaskQuizInput(string Type, string Question, string[] Options, int CorrectOptionId, string Explanation);
}
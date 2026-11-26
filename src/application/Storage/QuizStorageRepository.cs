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
        var writeStarted = false;
        try
        {
            if (operation == "storage_list") return await List(arguments, context.ChatId, timeout.Token);
            var dataset = arguments.TryGetProperty("dataset", out var name) ? name.GetString()! : "quiz_questions";
            _ = QuizStorageQuery.Fields(dataset);
            if (operation == "storage_get")
            {
                var response = await Read(arguments.GetProperty("id").GetString()!, context.ChatId, dataset, timeout.Token);
                var progress = dataset == "book_quizzes" ? await ReadProgress(context.ChatId, timeout.Token) : null;
                var view = View(response.Resource, response.ETag, progress);
                return Encoding.UTF8.GetByteCount(new StorageResult("ok", Data: view).ToJson()) > 12000
                    ? new("error", "budget_exceeded", "Quiz exceeds the 12000-byte tool result limit.")
                    : new("ok", Data: view);
            }
            if (dataset == "book_quiz_progress" || dataset == "book_quizzes" && operation is not ("storage_update" or "storage_shuffle_options")
                || operation == "storage_shuffle_options" && dataset != "book_quizzes")
                return new("error", "forbidden", "Progress is read-only; book quizzes support only guarded updates.");
            return await Mutate(operation, arguments, context, dataset, mutationIndex, () => writeStarted = true, timeout.Token);
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
                ? "Invalid Cosmos SQL query. Check SELECT, WHERE and ORDER BY using storage_schema."
                : "Storage operation did not complete successfully. For a conflict, read the current record before retrying.");
        }
        catch (Exception ex) when (!writeStarted && ex is (ArgumentException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException))
        {
            return new("error", operation == "storage_list" ? "invalid_query" : "validation_error",
                ex is ArgumentException ? ex.Message : "Invalid tool arguments. Follow storage_schema.");
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
        var query = QuizStorageQuery.Create(arguments);
        using var iterator = Container.GetItemQueryIterator<JsonElement>(query.Definition,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(chatId),
                MaxItemCount = query.Limit + 1,
                MaxConcurrency = 1,
                MaxBufferedItemCount = query.Limit + 1,
                EnableScanInQuery = false
            });
        var records = new List<object>();
        double charge = 0;
        var truncated = false;
        var progress = query.FullRecords && query.Dataset == "book_quizzes" ? await ReadProgress(chatId, cancellation) : null;
        // At most two pages, including empty pages. Never drain an unbounded iterator.
        for (var page = 0; page < 2 && iterator.HasMoreResults; page++)
        {
            var response = await iterator.ReadNextAsync(cancellation);
            charge += response.RequestCharge;
            if (charge > 50) return new("error", "budget_exceeded", "Query exceeded the 50 RU read budget. Narrow the predicate.");
            foreach (var record in response)
            {
                if (records.Count == query.Limit) { truncated = true; break; }
                var view = query.FullRecords ? View(record, record.GetProperty("_etag").GetString()!, progress) : record.Clone();
                records.Add(view);
                var candidate = new StorageResult("ok", Data: new { records, truncated = true, requestCharge = charge });
                if (Encoding.UTF8.GetByteCount(candidate.ToJson()) > 12000)
                {
                    records.RemoveAt(records.Count - 1);
                    truncated = true;
                    break;
                }
            }
            if (truncated || records.Count == query.Limit) break;
        }
        truncated |= iterator.HasMoreResults;
        if (query.Aggregate && (truncated || records.Count != 1))
            return new("error", "budget_exceeded", "COUNT did not complete within the read budget. Narrow the predicate; no approximate count is returned.");
        if (query.Scalar)
        {
            var scalarResult = new StorageResult("ok", Data: new
            {
                value = query.Exists ? (object)(records.Count != 0) : query.Aggregate ? records[0] : records.ToArray(),
                truncated = query.Exists ? false : truncated,
                requestCharge = charge,
            });
            return Encoding.UTF8.GetByteCount(scalarResult.ToJson()) > 12000
                ? new("error", "budget_exceeded", "Scalar query result exceeds the 12000-byte tool result limit.")
                : scalarResult;
        }
        var result = new StorageResult("ok", Data: new { records, truncated, requestCharge = charge });
        while (records.Count > 0 && Encoding.UTF8.GetByteCount(result.ToJson()) > 12000)
        {
            records.RemoveAt(records.Count - 1);
            truncated = true;
            result = new("ok", Data: new { records, truncated, requestCharge = charge });
        }
        return result;
    }

    public static void ValidatePredicate(string predicate) => QuizStorageQuery.ValidatePredicate(predicate);

    private async Task<ItemResponse<JsonElement>> Read(string id, long chatId, string dataset, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new ArgumentException("Invalid quiz ID.");
        var response = await Container.ReadItemAsync<JsonElement>(id, new PartitionKey(chatId), cancellationToken: cancellation);
        var record = response.Resource;
        var type = record.GetProperty("type").GetString();
        var supported = dataset switch
        {
            "quiz_questions" => QuizTypes.Contains(type),
            "book_quizzes" => type == "book-quiz" && record.GetProperty("seriesId").GetString() == BookQuiz.Series,
            "book_quiz_progress" => type == "book-quiz-progress" && id == "book-quiz-progress:" + BookQuiz.Series,
            _ => false,
        };
        if (record.GetProperty("chatId").GetInt64() != chatId || !supported)
            throw new ArgumentException("Record is not a supported quiz in this chat.");
        return response;
    }

    private async Task<StorageResult> Mutate(string operation, JsonElement arguments, StorageContext context,
        string dataset, int mutationIndex, Action markWriteStarted, CancellationToken cancellation)
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
        var hasMore = false;
        if (operation == "storage_shuffle_options")
        {
            var prepared = await PrepareOptionsShuffle(batch, arguments, context, cancellation);
            if (prepared.Error != null) return prepared.Error;
            ids.AddRange(prepared.Ids);
            hasMore = prepared.HasMore;
        }
        else if (operation == "storage_create")
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
            var current = await Read(id, context.ChatId, dataset, cancellation);
            var version = arguments.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(version) || current.ETag.Trim('"') != version.Trim('"'))
                return new("error", "conflict", "Version changed. Read the current record.");
            if (dataset == "book_quizzes")
            {
                var progress = await ReadProgress(context.ChatId, cancellation);
                var book = current.Resource.Deserialize<BookQuiz>(StorageResult.JsonOptions)!;
                if (progress.Sending || book.Sequence < progress.NextSequence)
                    return new("error", "forbidden", "Delivered book quizzes and a series with an in-flight delivery cannot be changed.");
                var replacement = ParseBookRecord(arguments.GetProperty("record"), book);
                // Guard the sender's progress snapshot in the same transaction as the quiz edit.
                if (progress.ETag == null) batch.CreateItem(progress);
                else batch.PatchItem(progress.Id, new[] { PatchOperation.Set("/nextSequence", progress.NextSequence) },
                    new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = progress.ETag });
                batch.PatchItem(id, EditablePatch(replacement.Type, replacement.Question, replacement.Options,
                    replacement.CorrectOptionId, replacement.Explanation),
                    new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = current.ETag });
            }
            else
            {
                var quiz = current.Resource.Deserialize<TimedTaskQuiz>()!;
                if (quiz.Posted || quiz.DeliveryPending)
                    return new("error", "forbidden", "Posted quizzes and quizzes with pending delivery cannot be changed.");
                var requestOptions = new TransactionalBatchItemRequestOptions { IfMatchEtag = current.ETag };
                if (operation == "storage_delete") batch.DeleteItem(id, requestOptions);
                else
                {
                    var replacement = ParseRecord(arguments.GetProperty("record"), id, context.ChatId);
                    batch.PatchItem(id, new[]
                    {
                    PatchOperation.Set("/type", replacement.Type),
                    PatchOperation.Set("/question", replacement.Question),
                    PatchOperation.Set("/options", replacement.Options),
                    PatchOperation.Set("/correctOptionId", replacement.CorrectOptionId),
                    PatchOperation.Set("/explanation", replacement.Explanation)
                }, new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = current.ETag });
                }
            }
            ids.Add(id);
        }
        else throw new ArgumentException("Unknown storage operation.");

        var result = operation == "storage_shuffle_options"
            ? new StorageResult("ok", Data: new { operation, count = ids.Count, ids, hasMore })
            : new StorageResult("ok", Data: new { operation, count = ids.Count, ids });
        // Receipt and mutation commit atomically in the existing /chatId partition.
        batch.CreateItem(new StorageOperationReceipt
        {
            Id = receiptId,
            ChatId = context.ChatId,
            Hash = hash,
            Result = JsonSerializer.SerializeToElement(result, StorageResult.JsonOptions)
        });
        markWriteStarted();
        using var response = await batch.ExecuteAsync(cancellation);
        if (!response.IsSuccessStatusCode)
            return new("error", response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                ? "conflict" : "storage_error", "Atomic write did not return success. Its outcome may be uncertain; do not retry automatically.");
        return result;
    }

    private async Task<(List<string> Ids, bool HasMore, StorageResult? Error)> PrepareOptionsShuffle(
        TransactionalBatch batch, JsonElement arguments, StorageContext context, CancellationToken cancellation)
    {
        var limit = arguments.GetProperty("limit").GetInt32();
        if (limit is < 1 or > 5) throw new ArgumentException("Shuffle batch limit must be between 1 and 5.");
        var progress = await ReadProgress(context.ChatId, cancellation);
        if (progress.Sending) return ([], false, new("error", "forbidden", "Book delivery is in flight; do not reshuffle now."));
        var requestId = $"{context.UserId}:{context.MessageId}";
        var query = new QueryDefinition($"SELECT TOP {limit + 1} * FROM c WHERE c.type = @type AND c.seriesId = @series"
            + " AND c.sequence >= @next AND (NOT IS_DEFINED(c.optionsShuffleRequest) OR c.optionsShuffleRequest != @request)"
            + " ORDER BY c.sequence")
            .WithParameter("@type", "book-quiz").WithParameter("@series", BookQuiz.Series)
            .WithParameter("@next", progress.NextSequence).WithParameter("@request", requestId);
        using var iterator = Container.GetItemQueryIterator<JsonElement>(query, requestOptions: new QueryRequestOptions
        { PartitionKey = new PartitionKey(context.ChatId), MaxItemCount = limit + 1, MaxConcurrency = 1, MaxBufferedItemCount = limit + 1, EnableScanInQuery = false });
        var records = new List<JsonElement>();
        double charge = 0;
        for (var page = 0; page < 2 && iterator.HasMoreResults && records.Count <= limit; page++)
        {
            var response = await iterator.ReadNextAsync(cancellation);
            charge += response.RequestCharge;
            if (charge > 50) return ([], false, new("error", "budget_exceeded", "Shuffle query exceeded its 50 RU budget; no changes committed."));
            records.AddRange(response.Select(record => record.Clone()));
        }
        if (records.Count == 0 && iterator.HasMoreResults)
            return ([], false, new("error", "budget_exceeded", "Shuffle query did not complete within two pages; no changes committed."));
        var ids = new List<string>();
        foreach (var record in records.Take(limit))
        {
            var book = record.Deserialize<BookQuiz>(StorageResult.JsonOptions)!;
            book.Validate();
            var answer = book.Options[book.CorrectOptionId];
            var target = RandomNumberGenerator.GetInt32(book.Options.Length - 1);
            if (target >= book.CorrectOptionId) target++;
            for (var index = book.Options.Length - 1; index > 0; index--)
            {
                var other = RandomNumberGenerator.GetInt32(index + 1);
                (book.Options[index], book.Options[other]) = (book.Options[other], book.Options[index]);
            }
            var actual = Array.IndexOf(book.Options, answer);
            (book.Options[actual], book.Options[target]) = (book.Options[target], book.Options[actual]);
            batch.PatchItem(book.Id, new[]
            {
                PatchOperation.Set("/options", book.Options), PatchOperation.Set("/correctOptionId", target),
                PatchOperation.Set("/optionsShuffleRequest", requestId)
            }, new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = record.GetProperty("_etag").GetString() });
            ids.Add(book.Id);
        }
        if (ids.Count != 0)
        {
            if (progress.ETag == null) batch.CreateItem(progress);
            else batch.PatchItem(progress.Id, new[] { PatchOperation.Set("/nextSequence", progress.NextSequence) },
                new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = progress.ETag });
        }
        return (ids, records.Count > limit || iterator.HasMoreResults, null);
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

    private async Task<BookQuizProgress> ReadProgress(long chatId, CancellationToken cancellation)
    {
        try
        {
            var response = await Container.ReadItemAsync<BookQuizProgress>("book-quiz-progress:" + BookQuiz.Series,
                new PartitionKey(chatId), cancellationToken: cancellation);
            response.Resource.ETag = response.ETag;
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new BookQuizProgress { ChatId = chatId };
        }
    }

    private static BookQuiz ParseBookRecord(JsonElement record, BookQuiz book)
    {
        StorageTools.RequireFields(record, "type", "question", "options", "correctOptionId", "explanation");
        var input = record.Deserialize<TimedTaskQuizInput>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (input.Type != "book-quiz") throw new ArgumentException("Book quiz type cannot be changed.");
        book.Question = input.Question;
        book.Options = input.Options;
        book.CorrectOptionId = input.CorrectOptionId;
        book.Explanation = input.Explanation;
        book.Validate();
        return book;
    }

    private static PatchOperation[] EditablePatch(string type, string question, string[] options, int correctOptionId, string explanation) =>
    [
        PatchOperation.Set("/type", type), PatchOperation.Set("/question", question), PatchOperation.Set("/options", options),
        PatchOperation.Set("/correctOptionId", correctOptionId), PatchOperation.Set("/explanation", explanation)
    ];

    private static object View(JsonElement record, string version, BookQuizProgress? progress)
    {
        if (record.GetProperty("type").GetString() == "book-quiz")
        {
            var book = record.Deserialize<BookQuiz>(StorageResult.JsonOptions)!;
            return new
            {
                id = book.Id,
                type = book.Type,
                seriesId = book.SeriesId,
                sequence = book.Sequence,
                chapter = book.Chapter,
                section = book.Section,
                printedPage = book.PrintedPage,
                pdfPage = book.PdfPage,
                question = book.Question,
                options = book.Options,
                correctOptionId = book.CorrectOptionId,
                explanation = book.Explanation,
                imageReference = book.ImageReference,
                imageDescription = book.ImageDescription,
                posted = book.Sequence < progress!.NextSequence,
                deliveryPending = progress.Sending && book.Sequence == progress.NextSequence,
                version = version.Trim('"'),
            };
        }
        if (record.GetProperty("type").GetString() == "book-quiz-progress")
        {
            var state = record.Deserialize<BookQuizProgress>(StorageResult.JsonOptions)!;
            return new
            {
                id = state.Id,
                type = state.Type,
                nextSequence = state.NextSequence,
                sending = state.Sending,
                lastSentUtc = state.LastSentUtc,
                version = version.Trim('"')
            };
        }
        var quiz = record.Deserialize<TimedTaskQuiz>()!;
        return new
        {
            id = quiz.Id,
            type = quiz.Type,
            question = quiz.Question,
            options = quiz.Options,
            correctOptionId = quiz.CorrectOptionId,
            explanation = quiz.Explanation,
            posted = quiz.Posted,
            deliveryPending = quiz.DeliveryPending,
            version = version.Trim('"'),
        };
    }

    private sealed record TimedTaskQuizInput(string Type, string Question, string[] Options, int CorrectOptionId, string Explanation);
}
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.Storage;
using User = Telegram.Bot.Types.User;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Conversation policy environment")]
public class StorageToolsLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Quiz = """{"type":"quiz-type-cs","question":"Which Linux command lists listening TCP sockets?","options":["ss -ltn","pwd","whoami"],"correctOptionId":0,"explanation":"ss -ltn lists listening TCP sockets with numeric addresses."}""";

    [SkippableFact]
    public async Task CosmosCrudIsAtomicRetrySafeScopedAndReturnsQueryErrors()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STORAGE_TOOLS_LIVE_TEST") == "1", "Storage live tests are not enabled.");
        await using var fixture = new Fixture();
        var tools = new StorageTools(fixture.Repository);
        var context = fixture.Context(1, "Create a quiz");
        var create = $$"""{"dataset":"quiz_questions","records":[{{Quiz}}]}""";
        var created = await tools.ExecuteAsync("storage_create", create, context, 1, fixture.Token);
        Assert.Equal("ok", created.Status);
        var replay = await tools.ExecuteAsync("storage_create", create, context, 1, fixture.Token);
        Assert.Equal(created.ToJson(), replay.ToJson());
        var changed = await tools.ExecuteAsync("storage_create", create.Replace("Linux command", "Linux utility"), context, 1, fixture.Token);
        Assert.Equal("conflict", changed.Code);
        var invalidBatch = $$"""{"dataset":"quiz_questions","records":[{{Quiz}},{{Quiz.Replace("\"correctOptionId\":0", "\"correctOptionId\":99")}}]}""";
        Assert.Equal("validation_error", (await tools.ExecuteAsync("storage_create", invalidBatch, context, 2, fixture.Token)).Code);

        var list = await tools.ExecuteAsync("storage_list",
            """{"dataset":"quiz_questions","predicate":"CONTAINS(LOWER(c.question), 'linux')","limit":1}""", context, 0, fixture.Token);
        Assert.Equal("ok", list.Status);
        Assert.False(Data(list).GetProperty("truncated").GetBoolean());
        var listed = Data(list).GetProperty("records")[0];
        var id = listed.GetProperty("id").GetString()!;
        var version = listed.GetProperty("version").GetString()!;
        var scheduler = new TimedTaskExecutionRepository(fixture.Cosmos, fixture.Options);
        var storageContainer = fixture.Cosmos.GetContainer(fixture.Options.Value.DatabaseId, fixture.Options.Value.TimedTasksContainerId);
        await storageContainer.PatchItemAsync<TimedTaskQuiz>(id, new PartitionKey(fixture.ChatId),
            new[] { PatchOperation.Set("/customMetadata", "preserve") }, cancellationToken: fixture.Token);
        var withMetadata = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "quiz_questions", id }), context, 0, fixture.Token);
        version = Data(withMetadata).GetProperty("version").GetString()!;

        var invalid = await tools.ExecuteAsync("storage_list",
            """{"dataset":"quiz_questions","predicate":"c.question =","limit":1}""", context, 0, fixture.Token);
        Assert.Equal("invalid_query", invalid.Code);
        Assert.DoesNotContain("AccountKey", invalid.ToJson());
        var forbidden = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "quiz_questions", id }), context with { UserId = 123 }, 0, fixture.Token);
        Assert.Equal("forbidden", forbidden.Code);
        var otherChat = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "quiz_questions", id }),
            context with { ChatId = fixture.ChatId - 1 }, 0, fixture.Token);
        Assert.Equal("not_found", otherChat.Code);

        var update = JsonSerializer.Serialize(new
        {
            dataset = "quiz_questions",
            id,
            version,
            record = JsonSerializer.Deserialize<JsonElement>(Quiz.Replace("TCP", "UDP"))
        });
        Assert.Equal("ok", (await tools.ExecuteAsync("storage_update", update, fixture.Context(2, "Update"), 1, fixture.Token)).Status);
        var rawUpdated = await storageContainer.ReadItemAsync<JsonElement>(id, new PartitionKey(fixture.ChatId),
            cancellationToken: fixture.Token);
        Assert.Equal("preserve", rawUpdated.Resource.GetProperty("customMetadata").GetString());
        using var staleBatch = await storageContainer.CreateTransactionalBatch(new PartitionKey(fixture.ChatId))
            .PatchItem(id, new[] { PatchOperation.Set("/question", "stale overwrite") },
                new TransactionalBatchPatchItemRequestOptions { IfMatchEtag = $"\"{version}\"" })
            .ExecuteAsync(fixture.Token);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleBatch.StatusCode);
        Assert.Equal("conflict", (await tools.ExecuteAsync("storage_update", update, fixture.Context(3, "Stale update"), 1, fixture.Token)).Code);
        var read = await tools.ExecuteAsync("storage_get", JsonSerializer.Serialize(new { dataset = "quiz_questions", id }),
            context, 0, fixture.Token);
        Assert.Contains("UDP", Data(read).GetProperty("question").GetString());
        var delete = JsonSerializer.Serialize(new
        {
            dataset = "quiz_questions",
            id,
            version = Data(read).GetProperty("version").GetString()
        });
        Assert.Equal("ok", (await tools.ExecuteAsync("storage_delete", delete, fixture.Context(4, "Delete"), 1, fixture.Token)).Status);
        Assert.Equal("not_found", (await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "quiz_questions", id }), context, 0, fixture.Token)).Code);
        Assert.Equal("ok", (await tools.ExecuteAsync("storage_create", create, fixture.Context(4, "create posted fixture"), 2, fixture.Token)).Status);
        var fixtureId = $"tool-quiz:{StorageTools.AdministratorUserId}:4:2:0";
        var container = fixture.Cosmos.GetContainer(fixture.Options.Value.DatabaseId, fixture.Options.Value.TimedTasksContainerId);
        await container.PatchItemAsync<TimedTaskQuiz>(fixtureId, new PartitionKey(fixture.ChatId),
            new[] { PatchOperation.Set("/customMetadata", "preserve after delivery") }, cancellationToken: fixture.Token);
        var beforeReservation = await container.ReadItemAsync<TimedTaskQuiz>(fixtureId,
            new PartitionKey(fixture.ChatId), cancellationToken: fixture.Token);
        var posted = await scheduler.GetQuiz(fixture.ChatId, TimedTaskQuiz.Type_ComputerScience, fixture.Token);
        Assert.NotNull(posted);
        var staleReservation = await Assert.ThrowsAsync<CosmosException>(() =>
            container.PatchItemAsync<TimedTaskQuiz>(fixtureId, new PartitionKey(fixture.ChatId),
                new[] { PatchOperation.Set("/deliveryPending", true) },
                new PatchItemRequestOptions { IfMatchEtag = beforeReservation.ETag }, fixture.Token));
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleReservation.StatusCode);
        var pendingRead = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "quiz_questions", id = posted.Id }), context, 0, fixture.Token);
        Assert.True(Data(pendingRead).GetProperty("deliveryPending").GetBoolean());
        var pendingDelete = JsonSerializer.Serialize(new
        {
            dataset = "quiz_questions",
            id = posted.Id,
            version = Data(pendingRead).GetProperty("version").GetString()
        });
        Assert.Equal("forbidden", (await tools.ExecuteAsync("storage_delete", pendingDelete,
            fixture.Context(4, "delete reserved fixture"), 3, fixture.Token)).Code);
        Assert.Equal("ok", (await tools.ExecuteAsync("storage_create", create, fixture.Context(4, "create next fixture"), 4, fixture.Token)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.GetQuiz(fixture.ChatId, TimedTaskQuiz.Type_ComputerScience, fixture.Token));
        await scheduler.CompleteQuiz(posted!, fixture.Token);
        var afterDelivery = await container.ReadItemAsync<JsonElement>(fixtureId, new PartitionKey(fixture.ChatId),
            cancellationToken: fixture.Token);
        Assert.Equal("preserve after delivery", afterDelivery.Resource.GetProperty("customMetadata").GetString());
        var postedRead = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "quiz_questions", id = posted!.Id }), context, 0, fixture.Token);
        var postedDelete = JsonSerializer.Serialize(new
        {
            dataset = "quiz_questions",
            id = posted.Id,
            version = Data(postedRead).GetProperty("version").GetString()
        });
        Assert.Equal("forbidden", (await tools.ExecuteAsync("storage_delete", postedDelete,
            fixture.Context(4, "delete posted fixture"), 3, fixture.Token)).Code);
        Assert.NotNull(await scheduler.GetQuiz(fixture.ChatId, TimedTaskQuiz.Type_ComputerScience, fixture.Token));
    }

    [SkippableFact]
    public async Task AzureToolChainCreatesQuizzesFromSyntheticTelegramUpdateWithoutTelegramTraffic()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STORAGE_TOOLS_LIVE_TEST") == "1", "Storage live tests are not enabled.");
        await using var fixture = new Fixture();
        var azure = AzureOpenAiSettings.FromEnvironment();
        var recording = new RecordingStorage(fixture.Repository);
        var client = azure.CreateChatCompletionClient(azure.ChatDeployment, storageTools: new StorageTools(recording));
        var bot = Substitute.For<ITelegramBotClient>();
        var whitelist = Substitute.For<IWhitelist>();
        whitelist.IsGroupAllowedToUseBot(fixture.ChatId).Returns(true);
        whitelist.GetSystemRoleMessage(fixture.ChatId).Returns("You are a helpful bot. Follow the user's request.");
        var conversations = Substitute.For<IConversationRepository>();
        conversations.Begin(fixture.ChatId, 1, Arg.Any<CancellationToken>()).Returns(new ConversationReceipt());
        var history = Substitute.For<ITelegramMessageRepository>();
        history.GetAllOrderByDateDescending(fixture.ChatId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TelegramMessage>().ToAsyncEnumerable());
        bot.SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(
            new Message
            {
                Id = 100,
                Chat = new Chat { Id = fixture.ChatId },
                Date = DateTime.UtcNow,
                From = new User { Id = 123, IsBot = true, FirstName = "Bot" }
            });
        var counter = new TokenCounter();
        var handler = new UpdateHandler(bot, NullLogger<UpdateHandler>.Instance, client, history, counter,
            new TooLongDidNotReadToday(history, counter, new SummaryOpenAiClient(client, azure.ChatDeployment),
                NullLogger<TooLongDidNotReadToday>.Instance, bot, whitelist, Substitute.For<ITimedTaskExecutionRepository>(), azure),
            whitelist, new GenerateImage(new ImageOpenAiClient(null), NullLogger<GenerateImage>.Instance, bot),
            new TestUsers(), conversations,
            new ConversationPolicy(conversations, new ConversationPolicyChats(), NullLogger<ConversationPolicy>.Instance,
                new DecisionsClient(new HttpClient(), NullLogger<DecisionsClient>.Instance, new AzureDecisionsSettings())),
            new PersonalSettings(bot, NullLogger<PersonalSettings>.Instance, azure), azure);

        await handler.Handle(new Update
        {
            Message = new Message
            {
                Id = 1,
                Chat = new Chat { Id = fixture.ChatId, Type = ChatType.Private },
                From = new User { Id = StorageTools.AdministratorUserId, FirstName = "Test administrator" },
                Date = DateTime.UtcNow,
                Text = "Add exactly 3 computer-science quiz questions about Linux networking (DNS, sockets and routing) to this chat's storage. Check existing questions first. Use English."
            },
        }, fixture.Token);

        Assert.Contains("storage_list", recording.Operations);
        Assert.True(recording.Operations.Contains("storage_create"),
            "No create; " + string.Join("; ", recording.Arguments.Select(arguments => arguments.GetRawText()))
            + "; results: " + string.Join("; ", recording.Results.Select(result => result.ToJson())));
        Assert.All(recording.Results, result => Assert.True(result.Status == "ok"
            || result.Code is "invalid_query" or "validation_error", result.ToJson()));
        var result = await fixture.Repository.ExecuteAsync("storage_list",
            JsonSerializer.Deserialize<JsonElement>("""{"predicate":"","limit":10}"""), fixture.Context(1, "verify"), 0, fixture.Token);
        Assert.Equal(3, Data(result).GetProperty("records").GetArrayLength());
        await bot.Received(1).SendRequest<Message>(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
        await history.Received(2).Create(Arg.Any<TelegramMessage>(), Arg.Any<CancellationToken>());

        var id = Data(result).GetProperty("records")[0].GetProperty("id").GetString()!;
        var expectedQuestion = "Linux quiz: " + Data(result).GetProperty("records")[0].GetProperty("question").GetString();
        var updateRequest = $"Update quiz {id}: change only its question to exactly {JsonSerializer.Serialize(expectedQuestion)} and keep its answer options and explanation.";
        var updated = await client.CompleteAsync(
            azure.Chat.CreateRequest([ChatMessage.FromUser(updateRequest)], azure.ChatDeployment)
                with
            { StorageContext = fixture.Context(2, updateRequest) }, fixture.Token);
        Assert.True(updated.Successful, $"{updated.ErrorType}: {updated.ErrorMessage}; "
            + string.Join(", ", recording.Operations.Zip(recording.Results, (operation, result) => $"{operation}:{result.Status}:{result.Code}:{result.Message}"))
            + "; arguments: " + string.Join("; ", recording.Arguments.Select(arguments => arguments.GetRawText())));
        Assert.Contains("storage_get", recording.Operations);
        Assert.Contains("storage_update", recording.Operations);
        var read = await fixture.Repository.ExecuteAsync("storage_get",
            JsonSerializer.SerializeToElement(new { id }), fixture.Context(2, "verify"), 0, fixture.Token);
        Assert.Equal(expectedQuestion, Data(read).GetProperty("question").GetString());

        var deleteRequest = $"Delete quiz {id} from storage. This is an explicit deletion request for that exact quiz.";
        var deleted = await client.CompleteAsync(
            azure.Chat.CreateRequest([ChatMessage.FromUser(deleteRequest)], azure.ChatDeployment)
                with
            { StorageContext = fixture.Context(3, deleteRequest) }, fixture.Token);
        Assert.True(deleted.Successful, $"{deleted.ErrorType}: {deleted.ErrorMessage}; "
            + string.Join(", ", recording.Operations.Zip(recording.Results, (operation, result) => $"{operation}:{result.Status}:{result.Code}:{result.Message}")));
        Assert.True(recording.Operations.Contains("storage_delete"), "No delete; reply: " + deleted.Content
            + "; arguments: " + string.Join("; ", recording.Arguments.Select(arguments => arguments.GetRawText())));
        Assert.Equal("not_found", (await fixture.Repository.ExecuteAsync("storage_get",
            JsonSerializer.SerializeToElement(new { id }), fixture.Context(3, "verify"), 0, fixture.Token)).Code);
    }

    [SkippableFact]
    public async Task AzureModelReceivesAndCorrectsRealCosmosQueryError()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STORAGE_TOOLS_LIVE_TEST") == "1", "Storage live tests are not enabled.");
        await using var fixture = new Fixture();
        var recording = new RecordingStorage(fixture.Repository);
        var azure = AzureOpenAiSettings.FromEnvironment();
        var client = azure.CreateChatCompletionClient(azure.ChatDeployment, storageTools: new StorageTools(recording));
        const string request = "For a query-recovery test, call storage_list on quiz_questions FIRST with predicate exactly 'c.question =' and limit 1. It is deliberately malformed. After the error, correct it to a valid query and report whether quiz_questions mentions TCP. Do not create or change anything.";
        var result = await client.CompleteAsync(azure.Chat.CreateRequest([ChatMessage.FromUser(request)], azure.ChatDeployment)
            with
        { StorageContext = fixture.Context(1, request) }, fixture.Token);
        Assert.True(result.Successful, $"{result.ErrorType}: {result.ErrorMessage}; "
            + string.Join(", ", recording.Operations.Zip(recording.Results, (operation, item) => $"{operation}:{item.Status}:{item.Code}:{item.Message}"))
            + "; arguments: " + string.Join("; ", recording.Arguments.Select(arguments => arguments.GetRawText())));
        Assert.Contains(recording.Results, result => result.Code == "invalid_query");
        Assert.Contains(recording.Results, result => result.Status == "ok");
        Assert.All(recording.Operations, operation => Assert.Equal("storage_list", operation));
    }

    [SkippableFact]
    public async Task CosmosSelectsScalarsAndProjectionsAndGuardsBookEditsAgainstTheSender()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STORAGE_TOOLS_LIVE_TEST") == "1", "Storage live tests are not enabled.");
        await using var fixture = new Fixture();
        await fixture.SeedBooks(18);
        var tools = new StorageTools(fixture.Repository);
        var context = fixture.Context(1, "Check and shuffle Linux quizzes");
        var count = await tools.ExecuteAsync("storage_list",
            """{"dataset":"book_quizzes","select":"VALUE COUNT(1)","predicate":"c.sequence >= 6 AND c.sequence < 11 AND c.correctOptionId = 0","orderBy":null,"limit":1}""",
            context, 0, fixture.Token);
        Assert.Equal("ok", count.Status);
        Assert.Equal(5, Data(count).GetProperty("value").GetInt32());
        Assert.True(Encoding.UTF8.GetByteCount(count.ToJson()) < 500);
        var exists = await tools.ExecuteAsync("storage_list",
            """{"dataset":"book_quizzes","select":"VALUE true","predicate":"c.sequence = 999","orderBy":null,"limit":1}""",
            context, 0, fixture.Token);
        Assert.False(Data(exists).GetProperty("value").GetBoolean());
        var latest = await tools.ExecuteAsync("storage_list",
            """{"dataset":"book_quizzes","select":"c.id, c.sequence, c.correctOptionId","predicate":"c.sequence < 11","orderBy":"c.sequence DESC","limit":5}""",
            context, 0, fixture.Token);
        Assert.Equal("ok", latest.Status);
        var rows = Data(latest).GetProperty("records").EnumerateArray().ToArray();
        Assert.Equal(new[] { 10, 9, 8, 7, 6 }, rows.Select(row => row.GetProperty("sequence").GetInt32()));
        Assert.All(rows, row => Assert.False(row.TryGetProperty("question", out _)));
        var ordinary = await tools.ExecuteAsync("storage_list",
            """{"dataset":"quiz_questions","select":"VALUE COUNT(1)","predicate":"","orderBy":null,"limit":1}""",
            context, 0, fixture.Token);
        Assert.Equal(0, Data(ordinary).GetProperty("value").GetInt32());

        var container = fixture.Container;
        var snapshot = await container.ReadItemAsync<BookQuizProgress>("book-quiz-progress:" + BookQuiz.Series,
            new PartitionKey(fixture.ChatId), cancellationToken: fixture.Token);
        snapshot.Resource.ETag = snapshot.ETag;
        var next = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "book_quizzes", id = BookQuiz.QuizId(11) }), context, 0, fixture.Token);
        var record = Data(next);
        var options = record.GetProperty("options").EnumerateArray().Select(option => option.GetString()!).ToArray();
        var update = JsonSerializer.Serialize(new
        {
            dataset = "book_quizzes",
            id = BookQuiz.QuizId(11),
            version = record.GetProperty("version").GetString(),
            record = new
            {
                type = "book-quiz",
                question = record.GetProperty("question").GetString(),
                options = options.Skip(1).Append(options[0]).ToArray(),
                correctOptionId = 3,
                explanation = record.GetProperty("explanation").GetString()
            },
        });
        Assert.Equal("ok", (await tools.ExecuteAsync("storage_update", update, context, 1, fixture.Token)).Status);
        var updated = await container.ReadItemAsync<JsonElement>(BookQuiz.QuizId(11), new PartitionKey(fixture.ChatId),
            cancellationToken: fixture.Token);
        Assert.Equal("preserve", updated.Resource.GetProperty("customMetadata").GetString());
        Assert.Equal(options[0], updated.Resource.GetProperty("options")[3].GetString());
        Assert.Equal(11, updated.Resource.GetProperty("sequence").GetInt32());
        Assert.Equal(1, updated.Resource.GetProperty("chapter").GetInt32());
        Assert.Equal("1.1", updated.Resource.GetProperty("section").GetString());
        Assert.Equal(16, updated.Resource.GetProperty("printedPage").GetInt32());
        Assert.Equal(26, updated.Resource.GetProperty("pdfPage").GetInt32());

        var sender = new BookQuizRepository(fixture.Cosmos, fixture.Options);
        snapshot.Resource.Sending = true;
        var staleReservation = await Assert.ThrowsAsync<CosmosException>(() => sender.Save(snapshot.Resource, fixture.Token));
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleReservation.StatusCode);
        var progress = await container.ReadItemAsync<BookQuizProgress>(snapshot.Resource.Id, new PartitionKey(fixture.ChatId),
            cancellationToken: fixture.Token);
        Assert.Equal(11, progress.Resource.NextSequence);
        Assert.False(progress.Resource.Sending);
        Assert.Equal(snapshot.Resource.LastSentUtc, progress.Resource.LastSentUtc);

        var delivered = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "book_quizzes", id = BookQuiz.QuizId(10) }), context, 0, fixture.Token);
        var deliveredUpdate = update.Replace(BookQuiz.QuizId(11), BookQuiz.QuizId(10))
            .Replace(record.GetProperty("version").GetString()!, Data(delivered).GetProperty("version").GetString()!);
        Assert.Equal("forbidden", (await tools.ExecuteAsync("storage_update", deliveredUpdate, fixture.Context(2, "edit delivered"), 1, fixture.Token)).Code);

        var reservation = await sender.TryBegin(fixture.ChatId, DateTime.UtcNow, fixture.Token);
        Assert.NotNull(reservation);
        Assert.Equal(options[0], reservation!.Value.Quiz.Options[3]);
        var current = await tools.ExecuteAsync("storage_get",
            JsonSerializer.Serialize(new { dataset = "book_quizzes", id = BookQuiz.QuizId(11) }), context, 0, fixture.Token);
        var inFlightUpdate = update.Replace(record.GetProperty("version").GetString()!, Data(current).GetProperty("version").GetString()!);
        Assert.Equal("forbidden", (await tools.ExecuteAsync("storage_update", inFlightUpdate, fixture.Context(3, "edit in flight"), 1, fixture.Token)).Code);
        Assert.Equal("forbidden", (await tools.ExecuteAsync("storage_shuffle_options",
            """{"dataset":"book_quizzes","limit":5}""", fixture.Context(3, "shuffle in flight"), 2, fixture.Token)).Code);
        reservation.Value.Progress.Sending = false;
        await sender.Save(reservation.Value.Progress, fixture.Token);

        var shuffleContext = fixture.Context(4, "Shuffle upcoming options");
        const string shuffleArguments = """{"dataset":"book_quizzes","limit":5}""";
        var first = await tools.ExecuteAsync("storage_shuffle_options", shuffleArguments, shuffleContext, 1, fixture.Token);
        Assert.Equal("ok", first.Status);
        Assert.Equal(5, Data(first).GetProperty("count").GetInt32());
        Assert.True(Data(first).GetProperty("hasMore").GetBoolean());
        var firstOptions = (await container.ReadItemAsync<BookQuiz>(BookQuiz.QuizId(11), new PartitionKey(fixture.ChatId),
            cancellationToken: fixture.Token)).Resource.Options;
        var retry = await tools.ExecuteAsync("storage_shuffle_options", shuffleArguments, shuffleContext, 1, fixture.Token);
        Assert.Equal(first.ToJson(), retry.ToJson());
        Assert.Equal(firstOptions, (await container.ReadItemAsync<BookQuiz>(BookQuiz.QuizId(11), new PartitionKey(fixture.ChatId),
            cancellationToken: fixture.Token)).Resource.Options);
        var second = await tools.ExecuteAsync("storage_shuffle_options", shuffleArguments, shuffleContext, 2, fixture.Token);
        Assert.Equal("ok", second.Status);
        Assert.Equal(3, Data(second).GetProperty("count").GetInt32());
        Assert.False(Data(second).GetProperty("hasMore").GetBoolean());
        Assert.Equal(firstOptions, (await container.ReadItemAsync<BookQuiz>(BookQuiz.QuizId(11), new PartitionKey(fixture.ChatId),
            cancellationToken: fixture.Token)).Resource.Options);
    }

    [SkippableFact]
    public async Task ExactRussianLinuxQuizRequestCountsDeliveredAnswersAndReshufflesWithoutContextOverflow()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("STORAGE_TOOLS_LIVE_TEST") == "1", "Storage live tests are not enabled.");
        await using var fixture = new Fixture();
        await fixture.SeedBooks(18);
        var before = new Dictionary<int, BookQuiz>();
        for (var sequence = 1; sequence <= 18; sequence++)
            before.Add(sequence, (await fixture.Container.ReadItemAsync<BookQuiz>(BookQuiz.QuizId(sequence),
                new PartitionKey(fixture.ChatId), cancellationToken: fixture.Token)).Resource);
        var recording = new RecordingStorage(fixture.Repository);
        using var transport = new RecordingTransport();
        using var http = new HttpClient(transport);
        var azure = AzureOpenAiSettings.FromEnvironment();
        var client = azure.CreateChatCompletionClient(azure.ChatDeployment, http, new StorageTools(recording));
        const string request = "Что-то послдение 5 квизов с правильным ответом под номером 1. Совпадение? Проверь и перетасуй квиз про линукс";
        var completionRequest = azure.Chat.CreateRequest(
            [ChatMessage.FromSystem("You are a helpful Telegram assistant. Follow the current user's request."), ChatMessage.FromUser(request)],
            azure.ChatDeployment) with
        { StorageContext = fixture.Context(1, request) };
        var completion = await client.CompleteAsync(completionRequest, fixture.Token);
        var diagnostics = string.Join("; ", recording.Operations.Zip(recording.Arguments,
            (operation, arguments) => $"{operation}:{arguments.GetRawText()}"));
        Assert.True(completion.Successful, $"{completion.ErrorType}: {completion.ErrorMessage}; {diagnostics}");
        var results = string.Join("; ", recording.Results.Select(result => result.ToJson()));
        Assert.True(recording.Arguments.Any(arguments => arguments.TryGetProperty("select", out var select)
            && select.GetString()?.Contains("COUNT", StringComparison.OrdinalIgnoreCase) == true),
            $"No scalar count: {completion.Content}; {diagnostics}; results: {results}");
        Assert.True(recording.Results.Any(result => result.Status == "ok" && Data(result).TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.Number && value.GetInt32() == 5),
            $"No count of 5: {completion.Content}; {diagnostics}; results: {results}");
        Assert.True(recording.Operations.Contains("storage_shuffle_options"), $"No options shuffle: {completion.Content}; {diagnostics}");
        Assert.DoesNotContain("storage_update", recording.Operations);
        Assert.DoesNotContain("storage_create", recording.Operations);
        Assert.DoesNotContain("storage_delete", recording.Operations);
        Assert.All(recording.Results, result => Assert.DoesNotContain("\"options\":", result.ToJson()));
        var counter = new TokenCounter();
        Assert.All(transport.Requests, body => Assert.True(counter.Count(body) <= completionRequest.InputTokenLimit,
            "A model request exceeded the input budget."));
        Assert.True(recording.Results.Sum(result => Encoding.UTF8.GetByteCount(result.ToJson())) < 8000,
            "The exact request accumulated excessive storage output.");
        output.WriteLine($"AI requests: {transport.Requests.Count}; total tokens: {completion.TotalTokens}; "
            + $"max serialized input tokens: {transport.Requests.Max(counter.Count)}; "
            + $"storage output bytes: {recording.Results.Sum(result => Encoding.UTF8.GetByteCount(result.ToJson()))}");

        var shuffled = 0;
        for (var sequence = 1; sequence <= 18; sequence++)
        {
            var after = (await fixture.Container.ReadItemAsync<BookQuiz>(BookQuiz.QuizId(sequence),
                new PartitionKey(fixture.ChatId), cancellationToken: fixture.Token)).Resource;
            Assert.Equal(before[sequence].Question, after.Question);
            Assert.Equal(before[sequence].Id, after.Id);
            Assert.Equal(before[sequence].Sequence, after.Sequence);
            Assert.Equal(before[sequence].SeriesId, after.SeriesId);
            Assert.Equal(before[sequence].ImageReference, after.ImageReference);
            Assert.Equal(before[sequence].ImageDescription, after.ImageDescription);
            Assert.Equal(before[sequence].Explanation, after.Explanation);
            Assert.Equal(before[sequence].Chapter, after.Chapter);
            Assert.Equal(before[sequence].Section, after.Section);
            Assert.Equal(before[sequence].PrintedPage, after.PrintedPage);
            Assert.Equal(before[sequence].PdfPage, after.PdfPage);
            Assert.Equal(before[sequence].Options.Order(), after.Options.Order());
            Assert.Equal(before[sequence].Options[before[sequence].CorrectOptionId], after.Options[after.CorrectOptionId]);
            if (sequence < 11)
            {
                Assert.Equal(before[sequence].Options, after.Options);
                Assert.Equal(before[sequence].CorrectOptionId, after.CorrectOptionId);
            }
            else if (!before[sequence].Options.SequenceEqual(after.Options))
            {
                shuffled++;
                Assert.NotEqual(0, after.CorrectOptionId);
            }
        }
        Assert.Equal(8, shuffled);
        var progress = await fixture.Container.ReadItemAsync<BookQuizProgress>("book-quiz-progress:" + BookQuiz.Series,
            new PartitionKey(fixture.ChatId), cancellationToken: fixture.Token);
        Assert.Equal(11, progress.Resource.NextSequence);
        Assert.False(progress.Resource.Sending);
    }

    private static JsonElement Data(StorageResult result) => JsonSerializer.SerializeToElement(result.Data, StorageResult.JsonOptions);

    private sealed class RecordingStorage(IQuizStorage inner) : IQuizStorage
    {
        public List<string> Operations { get; } = [];
        public List<StorageResult> Results { get; } = [];
        public List<JsonElement> Arguments { get; } = [];
        public async Task<StorageResult> ExecuteAsync(string operation, JsonElement arguments, StorageContext context,
            int mutationIndex, CancellationToken cancellation)
        {
            Operations.Add(operation);
            Arguments.Add(arguments.Clone());
            var result = await inner.ExecuteAsync(operation, arguments, context, mutationIndex, cancellation);
            Results.Add(result);
            return result;
        }
    }

    private sealed class RecordingTransport : DelegatingHandler
    {
        public List<string> Requests { get; } = [];
        public RecordingTransport() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public long ChatId { get; } = -8_000_000_000_000_000L - Random.Shared.NextInt64(100_000_000_000L);
        public CosmosClient Cosmos { get; }
        public IOptions<CosmosDbOptions> Options { get; }
        public QuizStorageRepository Repository { get; }
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        public CancellationToken Token => timeout.Token;
        public Container Container => Cosmos.GetContainer(Options.Value.DatabaseId, Options.Value.TimedTasksContainerId);

        public async Task SeedBooks(int count = 12)
        {
            string[] options = ["read() - чтение байтов из файлового дескриптора", "write() - запись байтов в файловый дескриптор",
                "fork() - создание дочернего процесса", "execve() - запуск новой программы"];
            for (var sequence = 1; sequence <= count; sequence++)
            {
                var correct = sequence < 6 ? sequence % 4 : 0;
                var quiz = new BookQuiz
                {
                    Id = BookQuiz.QuizId(sequence),
                    ChatId = ChatId,
                    Sequence = sequence,
                    Chapter = 1,
                    Section = "1.1",
                    PrintedPage = 5 + sequence,
                    PdfPage = 15 + sequence,
                    Question = $"Linux, вопрос {sequence}: какой системный вызов соответствует описанию «{options[correct]}»?",
                    Options = options.ToArray(),
                    CorrectOptionId = correct,
                    Explanation = $"Правильный ответ: {options[correct]}.",
                };
                quiz.Validate();
                await Container.CreateItemAsync(quiz, new PartitionKey(ChatId), cancellationToken: Token);
            }
            await Container.CreateItemAsync(new BookQuizProgress
            { ChatId = ChatId, NextSequence = 11, LastSentUtc = DateTime.UtcNow.AddDays(-1) }, new PartitionKey(ChatId), cancellationToken: Token);
            await Container.PatchItemAsync<BookQuiz>(BookQuiz.QuizId(11), new PartitionKey(ChatId),
                new[] { PatchOperation.Set("/customMetadata", "preserve") }, cancellationToken: Token);
        }

        public Fixture()
        {
            static string Required(string name) => Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException($"{name} is required for noninteractive live storage tests.");
            Options = Microsoft.Extensions.Options.Options.Create(new CosmosDbOptions
            {
                DatabaseId = Required("CosmosDatabase"),
                TimedTasksContainerId = Required("CosmosTimedTasksContainer"),
            });
            Cosmos = new CosmosClient(Required("CosmosDbConnectionString"), new CosmosClientOptions
            {
                Serializer = new CosmosSystemTextJsonSerializer(),
                MaxRetryAttemptsOnRateLimitedRequests = 1,
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(15),
            });
            Repository = new(Cosmos, Options, NullLogger<QuizStorageRepository>.Instance);
        }

        public StorageContext Context(int messageId, string request) => new(ChatId, StorageTools.AdministratorUserId, messageId, request);

        public async ValueTask DisposeAsync()
        {
            // Only deterministic fixture IDs in this unique synthetic partition are eligible for cleanup.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var container = Cosmos.GetContainer(Options.Value.DatabaseId, Options.Value.TimedTasksContainerId);
            try
            {
                var allowedIds = new HashSet<string>();
                allowedIds.UnionWith(Enumerable.Range(1, 18).Select(BookQuiz.QuizId));
                allowedIds.Add("book-quiz-progress:" + BookQuiz.Series);
                allowedIds.UnionWith(new[] { $"quiz-delivery-lock:{TimedTaskQuiz.Type_ComputerScience}",
                    $"quiz-delivery-lock:{TimedTaskQuiz.Type_Films}", $"quiz-delivery-lock:{TimedTaskQuiz.Type_VideoGames}" });
                for (var message = 1; message <= 4; message++)
                    for (var operation = 1; operation <= 12; operation++)
                    {
                        var ids = Enumerable.Range(0, 5).Select(index => $"tool-quiz:{StorageTools.AdministratorUserId}:{message}:{operation}:{index}")
                            .Append($"storage-operation:{StorageTools.AdministratorUserId}:{message}:{operation}");
                        allowedIds.UnionWith(ids);
                    }
                using var iterator = container.GetItemQueryIterator<JsonElement>("SELECT TOP 100 c.id FROM c",
                    requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(ChatId), MaxItemCount = 100 });
                while (iterator.HasMoreResults)
                    foreach (var row in await iterator.ReadNextAsync(cleanup.Token))
                    {
                        var id = row.GetProperty("id").GetString()!;
                        if (!allowedIds.Contains(id)) throw new InvalidOperationException("Unexpected record in fixture partition; refusing cleanup.");
                        try { await container.DeleteItemAsync<JsonElement>(id, new PartitionKey(ChatId), cancellationToken: cleanup.Token); }
                        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
                    }
            }
            finally { Cosmos.Dispose(); timeout.Dispose(); }
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.Storage;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class StorageToolsTests
{
    [Theory]
    [InlineData("'secret-value'")]
    [InlineData("'secret-value")]
    [InlineData("\"secret-value\\\"still-secret\"")]
    [InlineData("secret_value")]
    public async Task DiagnosticsRecordToolShapeWithoutUserData(string value)
    {
        using var logs = new HttpLogCapture();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var repository = Substitute.For<IQuizStorage>();
        repository.ExecuteAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new StorageResult("ok", Data: new { records = new[] { new { question = "secret-record" } } }));
        var arguments = JsonSerializer.Serialize(new
        { dataset = "quiz_questions", select = "c.id", predicate = $"c.question = {value} AND c.correctOptionId = 1234567", orderBy = (string?)null, limit = 1 });
        using var handler = new SequenceHandler(ToolResponse("one", "storage_list", arguments), TextResponse("secret-reply"));
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http, new StorageTools(repository),
            factory.CreateLogger<OpenAiChatCompletionClient>());

        var result = await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("secret-user-message")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "secret-user-message") });

        Assert.True(result.Successful);
        Assert.True(logs.Contains("AI storage response"));
        Assert.True(logs.Contains("AI storage tool"));
        Assert.False(logs.Contains("AI storage abort"));
        Assert.True(logs.Contains("c.question ="));
        Assert.False(logs.Contains("secret-value"));
        Assert.False(logs.Contains("still-secret"));
        Assert.False(logs.Contains("secret_value"));
        Assert.False(logs.Contains("secret-record"));
        Assert.False(logs.Contains("secret-reply"));
        Assert.False(logs.Contains("secret-user-message"));
        Assert.False(logs.Contains("1234567"));
        Assert.False(logs.Contains("test-key"));
    }

    [Fact]
    public async Task DiagnosticsIdentifyReasoningAndPerResponseOutputExhaustion()
    {
        using var logs = new HttpLogCapture();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var response = JsonNode.Parse(TextResponse("secret-partial-reply"))!;
        response["choices"]![0]!["finish_reason"] = "length";
        response["usage"]!["completion_tokens"] = 8192;
        response["usage"]!["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 8192 };
        using var handler = new SequenceHandler(response.ToJsonString());
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create(new CompletionSettings(maxCompletionTokens: 8192));
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http,
            new StorageTools(Substitute.For<IQuizStorage>()), factory.CreateLogger<OpenAiChatCompletionClient>());

        var result = await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("check")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "check") });

        Assert.Equal("tool_budget_exceeded", result.ErrorType);
        Assert.True(logs.Contains("response_output_limit"));
        Assert.True(logs.Contains("ReasoningTokens=8192"));
        Assert.False(logs.Contains("secret-partial-reply"));
    }

    [Fact]
    public async Task ReviewedToolTurnAllowsTwo32KResponsesThenStopsAt64K()
    {
        using var logs = new HttpLogCapture();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var responses = Enumerable.Range(1, 2).Select(index =>
        {
            var response = JsonNode.Parse(ToolResponse(index.ToString(), "storage_catalog", "{}"))!;
            response["usage"]!["completion_tokens"] = 32768;
            return response.ToJsonString();
        }).ToArray();
        using var handler = new SequenceHandler(responses);
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create(new CompletionSettings(maxCompletionTokens: 32768, toolOutputTokens: 65536));
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http,
            new StorageTools(Substitute.For<IQuizStorage>()), factory.CreateLogger<OpenAiChatCompletionClient>());
        var result = await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("catalog")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "catalog") });

        Assert.Equal(65536, result.CompletionTokens);
        Assert.Equal("tool_budget_exceeded", result.ErrorType);
        Assert.True(logs.Contains("turn_output_limit"));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, body =>
            Assert.Equal(32768, JsonDocument.Parse(body).RootElement.GetProperty("max_completion_tokens").GetInt32()));
    }

    [Theory]
    [InlineData("CONTAINS(LOWER(c.question), 'linux') AND c.posted = false")]
    [InlineData("c.question = 'someone''s SELECT * FROM text'")]
    public void PredicateAllowsSupportedCosmosExpressions(string predicate) =>
        QuizStorageRepository.ValidatePredicate(predicate);

    [Theory]
    [InlineData("SELECT * FROM c")]
    [InlineData("c.chatId = -123")]
    [InlineData("c._etag = 'secret'")]
    [InlineData("true) OR true --")]
    [InlineData("EXISTS(SELECT VALUE x FROM x IN c.options)")]
    [InlineData(@"c.question = 'x\' OR c.posted = false OR c.question = '")]
    public void PredicateRejectsScopeEscapeAndUnsupportedQueries(string predicate) =>
        Assert.Throws<ArgumentException>(() => QuizStorageRepository.ValidatePredicate(predicate));

    [Theory]
    [InlineData("VALUE COUNT(1)", "", true)]
    [InlineData("COUNT(1) AS count", "", true)]
    [InlineData("c.id, c.sequence, c.correctOptionId", "c.sequence DESC", false)]
    [InlineData("VALUE true", "", false)]
    [InlineData("c.id, c.options, c.version", "", false)]
    [InlineData("VALUE {\"id\":c.id,\"options\":c.options,\"version\":c.version}", "", false)]
    public void SelectControlsCannotReplaceServerOwnedDatasetFilter(string select, string orderBy, bool aggregate)
    {
        var query = QuizStorageQuery.Create(JsonSerializer.SerializeToElement(new
        {
            dataset = "book_quizzes",
            select,
            predicate = "c.sequence >= 6 AND c.sequence < 11 AND c.correctOptionId = 0",
            orderBy,
            limit = 5,
        }));
        Assert.Contains("FROM c WHERE c.type = @book AND c.seriesId = @series AND (", query.Definition.QueryText);
        Assert.Equal(aggregate, query.Aggregate);
        Assert.DoesNotContain("TOP", aggregate ? query.Definition.QueryText : "");
        Assert.DoesNotContain("c.version", query.Definition.QueryText);
    }

    [Theory]
    [InlineData("SELECT VALUE COUNT(1) FROM c", "", "")]
    [InlineData("c._etag", "", "")]
    [InlineData("c.chatId", "", "")]
    [InlineData("c.id FROM c JOIN x IN c.options", "", "")]
    [InlineData("VALUE COUNT(1)", "", "c.sequence DESC")]
    [InlineData("VALUE c.id AS\talias", "", "")]
    [InlineData("c.id", "true) OR true --", "")]
    [InlineData("c.id", "_rid != ''", "")]
    [InlineData("c.id", "c.nextSequence = 1", "")]
    [InlineData("c.id", "", "c.sequence DESC OFFSET 0 LIMIT 1000")]
    [InlineData("c.id", "", "c.sequence DESC, c._ts DESC")]
    public void SelectControlsRejectInternalFieldsScopeEscapesAndUnboundedQueries(string select, string predicate, string orderBy) =>
        Assert.Throws<ArgumentException>(() => QuizStorageQuery.Create(JsonSerializer.SerializeToElement(new
        {
            dataset = "book_quizzes",
            select,
            predicate,
            orderBy,
            limit = 5,
        })));

    [Theory]
    [InlineData(false, true, "budget_exceeded", null)]
    [InlineData(false, false, "ok", false)]
    [InlineData(true, true, "ok", true)]
    public async Task ExistsNeverReturnsUnprovenNegativeResult(bool found, bool unfinished, string expected, bool? value)
    {
        var container = Substitute.For<Container>();
        var cosmos = Substitute.For<CosmosClient>();
        cosmos.GetContainer("test", "quizzes").Returns(container);
        var iterator = Substitute.For<FeedIterator<JsonElement>>();
        var response = Substitute.For<FeedResponse<JsonElement>>();
        response.RequestCharge.Returns(1);
        response.GetEnumerator().Returns(_ => (found ? new[] { JsonSerializer.SerializeToElement(true) } : Array.Empty<JsonElement>())
            .AsEnumerable().GetEnumerator());
        var more = true;
        iterator.HasMoreResults.Returns(_ => more);
        iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            more = unfinished;
            return Task.FromResult(response);
        });
        container.GetItemQueryIterator<JsonElement>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
        var repository = new QuizStorageRepository(cosmos, Microsoft.Extensions.Options.Options.Create(new CosmosDbOptions
        { DatabaseId = "test", TimedTasksContainerId = "quizzes" }), NullLogger<QuizStorageRepository>.Instance);

        var result = await repository.ExecuteAsync("storage_list", JsonSerializer.SerializeToElement(new
        {
            dataset = "book_quizzes",
            select = "VALUE true",
            predicate = "c.correctOptionId = 0",
            limit = 1,
        }), new(-1, StorageTools.AdministratorUserId, 1, "exists"), 0, default);

        Assert.Equal(expected, expected == "ok" ? result.Status : result.Code);
        if (value is null) Assert.Null(result.Data);
        else Assert.Equal(value, JsonSerializer.SerializeToElement(result.Data).GetProperty("value").GetBoolean());
        await iterator.Received(found || !unfinished ? 1 : 2).ReadNextAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScalarCountsAreCompleteOrFailWithoutReturningPartialData(bool unfinished)
    {
        var container = Substitute.For<Container>();
        var cosmos = Substitute.For<CosmosClient>();
        cosmos.GetContainer("test", "quizzes").Returns(container);
        var iterator = Substitute.For<FeedIterator<JsonElement>>();
        var response = Substitute.For<FeedResponse<JsonElement>>();
        response.RequestCharge.Returns(1);
        response.GetEnumerator().Returns(_ => (unfinished ? Array.Empty<JsonElement>() : new[] { JsonSerializer.SerializeToElement(5) })
            .AsEnumerable().GetEnumerator());
        var more = true;
        iterator.HasMoreResults.Returns(_ => more);
        iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            more = unfinished;
            return Task.FromResult(response);
        });
        container.GetItemQueryIterator<JsonElement>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
        var repository = new QuizStorageRepository(cosmos, Microsoft.Extensions.Options.Options.Create(new CosmosDbOptions
        { DatabaseId = "test", TimedTasksContainerId = "quizzes" }), NullLogger<QuizStorageRepository>.Instance);

        var result = await repository.ExecuteAsync("storage_list", JsonSerializer.SerializeToElement(new
        {
            dataset = "book_quizzes",
            select = "VALUE COUNT(1)",
            predicate = "c.correctOptionId = 0",
            limit = 1,
        }), new(-1, StorageTools.AdministratorUserId, 1, "count"), 0, default);

        if (unfinished)
        {
            Assert.Equal("budget_exceeded", result.Code);
            Assert.Null(result.Data);
        }
        else
        {
            Assert.Equal("ok", result.Status);
            Assert.Equal(5, JsonSerializer.SerializeToElement(result.Data).GetProperty("value").GetInt32());
        }
        await iterator.Received(unfinished ? 2 : 1).ReadNextAsync(Arg.Any<CancellationToken>());
        container.Received(1).GetItemQueryIterator<JsonElement>(Arg.Any<QueryDefinition>(), Arg.Any<string>(),
            Arg.Is<QueryRequestOptions>(options => options.PartitionKey == new PartitionKey(-1) && options.EnableScanInQuery == false));
    }

    [Fact]
    public async Task DispatcherRejectsUnauthorizedAndExtraArgumentsBeforeDal()
    {
        var repository = Substitute.For<IQuizStorage>();
        var tools = new StorageTools(repository);
        var unauthorized = await tools.ExecuteAsync("storage_list",
            """{"dataset":"quiz_questions","predicate":"","limit":5}""", new(-1, 123, 1, "list"), 0, default);
        var extra = await tools.ExecuteAsync("storage_list",
            """{"dataset":"quiz_questions","predicate":"","limit":5,"chatId":123}""",
            new(-1, StorageTools.AdministratorUserId, 1, "list"), 0, default);
        Assert.Equal("forbidden", unauthorized.Code);
        Assert.Equal("validation_error", extra.Code);
        await repository.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task DalReturnsValidationErrorForWrongArgumentTypes()
    {
        var repo = new QuizStorageRepository(null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<QuizStorageRepository>.Instance);
        var result = await repo.ExecuteAsync("storage_list",
            JsonSerializer.Deserialize<JsonElement>("""{"predicate":42,"limit":1}"""),
            new(-1, StorageTools.AdministratorUserId, 1, "list"), 0, default);
        Assert.Equal("invalid_query", result.Code);
    }

    [Theory]
    [InlineData("SC1001 syntax error; Authorization: secret-value https://private.example", "SC1001")]
    [InlineData("The order by query does not have a corresponding composite index. secret-value", "one sort field")]
    public async Task CosmosQueryErrorsGiveSafeActionableFeedback(string providerDetails, string expected)
    {
        var container = Substitute.For<Container>();
        var cosmos = Substitute.For<CosmosClient>();
        cosmos.GetContainer("test", "quizzes").Returns(container);
        var iterator = Substitute.For<FeedIterator<JsonElement>>();
        iterator.HasMoreResults.Returns(true);
        iterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromException<FeedResponse<JsonElement>>(new CosmosException(providerDetails, HttpStatusCode.BadRequest, 0, "activity", 1)));
        container.GetItemQueryIterator<JsonElement>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(iterator);
        var repository = new QuizStorageRepository(cosmos, Microsoft.Extensions.Options.Options.Create(new CosmosDbOptions
        { DatabaseId = "test", TimedTasksContainerId = "quizzes" }), NullLogger<QuizStorageRepository>.Instance);

        var result = await repository.ExecuteAsync("storage_list", JsonSerializer.SerializeToElement(new
        { dataset = "quiz_questions", select = "c.id", predicate = "c.question =", orderBy = (string?)null, limit = 1 }),
            new(-1, StorageTools.AdministratorUserId, 1, "query"), 0, default);

        Assert.Equal("invalid_query", result.Code);
        Assert.Contains(expected, result.Message);
        Assert.DoesNotContain("secret-value", result.ToJson());
        Assert.DoesNotContain("Authorization", result.ToJson());
        Assert.DoesNotContain("https://", result.ToJson());
    }

    [Fact]
    public async Task RecoveryExhaustionReportsInvalidCallsInsteadOfContextOverflow()
    {
        var repository = Substitute.For<IQuizStorage>();
        repository.ExecuteAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new StorageResult("error", "invalid_query", "SC1001: comparison needs a value."));
        using var handler = new SequenceHandler(Enumerable.Range(1, 4).Select(index =>
            ToolResponse(index.ToString(), "storage_list", """{"dataset":"quiz_questions","predicate":"c.question =","limit":1}""")).ToArray());
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http, new StorageTools(repository));

        var result = await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("query")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "query") });

        Assert.Equal("tool_query_recovery_exhausted", result.ErrorType);
        Assert.Contains("invalidCalls=3", result.ErrorMessage);
        Assert.Contains("lastTool=storage_list", result.ErrorMessage);
        Assert.Contains("lastCode=invalid_query", result.ErrorMessage);
        Assert.Equal(4, handler.Requests.Count);
        await repository.Received(3).ExecuteAsync("storage_list", Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), 0,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RoundExhaustionIsNotReportedAsContextOverflow()
    {
        using var handler = new SequenceHandler(Enumerable.Range(1, 8)
            .Select(index => ToolResponse(index.ToString(), "storage_catalog", "{}")).ToArray());
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http,
            new StorageTools(Substitute.For<IQuizStorage>()));

        var result = await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("catalog")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "catalog") });

        Assert.Equal("tool_round_limit", result.ErrorType);
        Assert.Contains("rounds=8", result.ErrorMessage);
        Assert.Equal(8, handler.Requests.Count);
    }

    [Fact]
    public async Task SdkLoopReturnsQueryErrorToModelAndPreservesToolCallIds()
    {
        var repository = Substitute.For<IQuizStorage>();
        repository.ExecuteAsync("storage_list", Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), 0, Arg.Any<CancellationToken>())
            .Returns(new StorageResult("error", "invalid_query", "Check predicate syntax."),
                new StorageResult("ok", Data: new { records = Array.Empty<object>() }));
        using var handler = new SequenceHandler(
            ToolResponse("bad", "storage_list", """{"dataset":"quiz_questions","predicate":"c.question =","limit":5}"""),
            ToolResponse("fixed", "storage_list", """{"dataset":"quiz_questions","predicate":"CONTAINS(c.question, 'linux')","limit":5}"""),
            TextResponse("No matches."));
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http, new StorageTools(repository));

        var result = await client.CompleteAsync(
            settings.Chat.CreateRequest([ChatMessage.FromUser("Find Linux quizzes.")], settings.ChatDeployment)
                with
            { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "Find Linux quizzes.") });

        Assert.True(result.Successful);
        Assert.Equal("No matches.", result.Content);
        using var second = JsonDocument.Parse(handler.Requests[1]);
        var messages = second.RootElement.GetProperty("messages");
        var tool = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "tool");
        Assert.Equal("bad", tool.GetProperty("tool_call_id").GetString());
        Assert.Contains("invalid_query", tool.GetProperty("content").GetString());
        var assistant = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "assistant");
        Assert.Equal("bad", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal(8, second.RootElement.GetProperty("tools").GetArrayLength());
        Assert.False(second.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
    }

    [Fact]
    public async Task OrdinaryChatDoesNotExposeStorageTools()
    {
        using var handler = new SequenceHandler(TextResponse("Hello"));
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http,
            new StorageTools(Substitute.For<IQuizStorage>()));
        await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("Hello")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, 123, 1, "Hello") });
        using var request = JsonDocument.Parse(handler.Requests.Single());
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task IntermediateToolResultsCountAgainstInputBudget()
    {
        var repository = Substitute.For<IQuizStorage>();
        repository.ExecuteAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new StorageResult("ok", Data: new { text = string.Concat(Enumerable.Repeat("abcdefghij ", 2000)) }));
        using var handler = new SequenceHandler(
            ToolResponse("one", "storage_list", """{"dataset":"quiz_questions","predicate":"","limit":1}"""),
            TextResponse("Should not be reached"));
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http, new StorageTools(repository));
        var result = await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("list")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "list"), InputTokenLimit = 4000 });
        Assert.Equal("tool_budget_exceeded", result.ErrorType);
        Assert.Single(handler.Requests);
        await repository.Received(1).ExecuteAsync("storage_list", Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), 0,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MutationConflictBlocksFurtherWrites()
    {
        var repository = Substitute.For<IQuizStorage>();
        repository.ExecuteAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new StorageResult("error", "conflict", "Already completed."));
        var args = """{"dataset":"quiz_questions","records":[]}""";
        using var handler = new SequenceHandler(ToolResponse("one", "storage_create", args),
            ToolResponse("two", "storage_create", args), TextResponse("Could not complete."));
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http, new StorageTools(repository));
        await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("Create quiz")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "Create quiz") });
        await repository.Received(1).ExecuteAsync("storage_create", Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), 1,
            Arg.Any<CancellationToken>());
        Assert.Contains("Further writes are blocked", handler.Requests[2]);
    }

    [Fact]
    public async Task ShuffleIsAMutationAndItsConflictBlocksFurtherWrites()
    {
        var repository = Substitute.For<IQuizStorage>();
        repository.ExecuteAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new StorageResult("error", "conflict", "Concurrent delivery."));
        using var handler = new SequenceHandler(
            ToolResponse("shuffle", "storage_shuffle_options", """{"dataset":"book_quizzes","limit":5}"""),
            ToolResponse("create", "storage_create", """{"dataset":"quiz_questions","records":[]}"""),
            TextResponse("Unable to shuffle during delivery."));
        using var http = new HttpClient(handler);
        var settings = TestAzureSettings.Create();
        var client = settings.CreateChatCompletionClient(settings.ChatDeployment, http, new StorageTools(repository));

        await client.CompleteAsync(settings.Chat.CreateRequest([ChatMessage.FromUser("Shuffle Linux options")], settings.ChatDeployment)
            with
        { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "Shuffle Linux options") });

        await repository.Received(1).ExecuteAsync("storage_shuffle_options", Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), 1,
            Arg.Any<CancellationToken>());
        await repository.DidNotReceive().ExecuteAsync("storage_create", Arg.Any<JsonElement>(), Arg.Any<StorageContext>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    private static string ToolResponse(string id, string name, string arguments) => JsonSerializer.Serialize(new
    {
        id = "completion",
        @object = "chat.completion",
        created = 1,
        model = "test-chat",
        choices = new[] { new { index = 0, finish_reason = "tool_calls",
            message = new { role = "assistant", tool_calls = new[] { new { id, type = "function", function = new { name, arguments } } } } } },
        usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 },
    });

    private static string TextResponse(string text) => JsonSerializer.Serialize(new
    {
        id = "completion",
        @object = "chat.completion",
        created = 1,
        model = "test-chat",
        choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = text } } },
        usage = new { prompt_tokens = 10, completion_tokens = 10, total_tokens = 20 },
    });

    private sealed class SequenceHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Requests.Count - 1], Encoding.UTF8, "application/json") };
        }
    }
}

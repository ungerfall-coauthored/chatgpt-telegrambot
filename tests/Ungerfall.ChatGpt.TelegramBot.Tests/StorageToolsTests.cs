using System.Net;
using System.Text;
using System.Text.Json;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Storage;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class StorageToolsTests
{
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
        Assert.Equal(7, second.RootElement.GetProperty("tools").GetArrayLength());
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
            with { StorageContext = new(-1, StorageTools.AdministratorUserId, 1, "list"), InputTokenLimit = 2000 });
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

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
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
public class StorageToolsLiveTests
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
        Assert.Equal(id, (await scheduler.GetQuiz(fixture.ChatId, TimedTaskQuiz.Type_ComputerScience, fixture.Token))!.Id);

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
        var posted = await scheduler.GetQuiz(fixture.ChatId, TimedTaskQuiz.Type_ComputerScience, fixture.Token);
        await scheduler.CompleteQuiz(posted!, fixture.Token);
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
        Assert.Contains("storage_create", recording.Operations);
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
            + string.Join(", ", recording.Operations.Zip(recording.Results, (operation, result) => $"{operation}:{result.Status}:{result.Code}:{result.Message}")));
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
        Assert.Contains("storage_delete", recording.Operations);
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
        const string request = "For a query-recovery test, call storage_list on quiz_questions FIRST with predicate exactly 'c.question =' and limit 1. It is deliberately malformed. After the error, correct it to a valid query and report whether Linux quizzes exist. Do not create or change anything.";
        var result = await client.CompleteAsync(azure.Chat.CreateRequest([ChatMessage.FromUser(request)], azure.ChatDeployment)
            with
        { StorageContext = fixture.Context(1, request) }, fixture.Token);
        Assert.True(result.Successful, result.ErrorType);
        Assert.Contains(recording.Results, result => result.Code == "invalid_query");
        Assert.Contains(recording.Results, result => result.Status == "ok");
        Assert.All(recording.Operations, operation => Assert.Equal("storage_list", operation));
    }

    private static JsonElement Data(StorageResult result) => JsonSerializer.SerializeToElement(result.Data, StorageResult.JsonOptions);

    private sealed class RecordingStorage(IQuizStorage inner) : IQuizStorage
    {
        public List<string> Operations { get; } = [];
        public List<StorageResult> Results { get; } = [];
        public async Task<StorageResult> ExecuteAsync(string operation, JsonElement arguments, StorageContext context,
            int mutationIndex, CancellationToken cancellation)
        {
            Operations.Add(operation);
            var result = await inner.ExecuteAsync(operation, arguments, context, mutationIndex, cancellation);
            Results.Add(result);
            return result;
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

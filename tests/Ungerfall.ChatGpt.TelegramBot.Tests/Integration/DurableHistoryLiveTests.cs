using System.Net;
using Microsoft.Azure.Cosmos;
using Ungerfall.ChatGpt.TelegramBot.History;
using System.Collections.ObjectModel;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("Conversation policy environment")]
public class DurableHistoryLiveTests
{
    private const string LimitedEvidence = @"(?i)по (?:доступн|имеющ|представленн)|на основе (?:доступн|имеющ|представленн|двух)|(?:архив|данн|сообщен|участник).{0,40}(?:огранич|неполн|недостаточ|не\s+все)|(?:огранич|неполн|не\s+все|част[ьи]|только).{0,40}(?:архив|данн|сообщен|участник)|выборк[аи]";

    private static bool DisclosesPeriodOrUncertainty(string answer) =>
        System.Text.RegularExpressions.Regex.IsMatch(answer, @"(?i)приблиз|примерн|ориентировоч|неопредел[её]н|условн")
        || (System.Text.RegularExpressions.Regex.IsMatch(answer, @"(?<!\d)(?:18|19|20|21)(?!\d)")
            && System.Text.RegularExpressions.Regex.IsMatch(answer, @"(?i)феврал|2022-02"));

    internal static bool HasLateFebruarySources(ToolCallObservationHandler observation)
    {
        foreach (var call in observation.Calls.Where(call => call.Name == "history_query").Reverse())
        {
            var result = observation.ToolResults.LastOrDefault(result => result.Id == call.Id);
            if (result == null) continue;
            using var document = System.Text.Json.JsonDocument.Parse(result.Content);
            var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "ok"
                || !root.TryGetProperty("data", out var data)
                || !data.TryGetProperty("rows", out var rows)
                || rows.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            var predicate = call.Arguments.GetProperty("predicate").GetString()!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(predicate, @"c\.date\s*>=\s*'2022-02-(?:1[8-9]|2[01])'")
                || !System.Text.RegularExpressions.Regex.IsMatch(predicate, @"c\.date\s*<\s*'2022-(?:02-2[7-9]|03-0[1-3])'")
                || System.Text.RegularExpressions.Regex.IsMatch(predicate, @"\b(?:OR|NOT)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return false;
            var sources = rows.EnumerateArray().Where(row => row.ValueKind == System.Text.Json.JsonValueKind.Object
                && row.TryGetProperty("text", out _) && row.TryGetProperty("userId", out _)
                && row.TryGetProperty("user", out _) && row.TryGetProperty("date", out _)).ToArray();
            return sources.Any(row => row.GetProperty("userId").GetInt64() == 42
                    && row.GetProperty("user").GetString() == "Алиса"
                    && row.GetProperty("date").GetString()!.StartsWith("2022-02-25", StringComparison.Ordinal)
                    && row.GetProperty("text").GetString() == "Привет! Как здорово! Ура! Пойдём гулять в парк. fixture-alpha")
                && sources.Any(row => row.GetProperty("userId").GetInt64() == 43
                    && row.GetProperty("user").GetString() == "Борис"
                    && row.GetProperty("date").GetString()!.StartsWith("2022-02-26", StringComparison.Ordinal)
                    && row.GetProperty("text").GetString() == "Считаю, что следует рассмотреть прогулку в парке. fixture-beta");
        }
        return false;
    }

    [SkippableFact]
    public async Task CanonicalBatchesReplayEditsDeletionConsentAndExactActivityUseRealCosmos()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DURABLE_HISTORY_LIVE_TEST") == "1",
            "Durable history live tests are not enabled.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var client = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new HistoryCosmosSerializer(), ConnectionMode = ConnectionMode.Gateway });
        var database = client.GetDatabase(Environment.GetEnvironmentVariable("CosmosDatabase"));
        Container? fixture = null;
        try
        {
            var serverless = false;
            try { await database.ReadThroughputAsync(cancellationToken: timeout.Token); }
            catch (CosmosException error) when (error.StatusCode == HttpStatusCode.BadRequest
                && (error.Message.Contains("not supported for serverless accounts", StringComparison.OrdinalIgnoreCase)
                    || error.Message.Contains("not allowed in serverless accounts", StringComparison.OrdinalIgnoreCase)))
            {
                serverless = true;
            }
            Assert.True(serverless, "No fixture may be created without serverless verification.");
            var name = "history-core-test-" + Guid.NewGuid().ToString("N");
            var properties = new ContainerProperties(name, "/chatId")
            {
                DefaultTimeToLive = 3600,
                VectorEmbeddingPolicy = new VectorEmbeddingPolicy(new Collection<Embedding>
                {
                    new() { Path = "/embedding", DataType = VectorDataType.Float32, Dimensions = 512, DistanceFunction = DistanceFunction.Cosine },
                }),
            };
            properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
            properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/embedding/*" });
            properties.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath { Path = "/embedding", Type = VectorIndexType.QuantizedFlat });
            fixture = (await database.CreateContainerAsync(
                properties,
                cancellationToken: timeout.Token)).Container;
            var options = new HistoryOptions
            {
                Enabled = true,
                Chats = new HashSet<long> { -1, -2 },
                MessagesContainer = name,
                ChunksContainer = name,
            };
            var repository = new HistoryRepository(client, database.Id, options);
            await repository.EnsureImportConsent(-1, 42, timeout.Token);
            var records = Enumerable.Range(1, 16).Select(index => new HistoryMessage
            {
                ChatId = -1,
                SourceChatId = -1,
                MessageId = index,
                UserId = 42,
                User = "Synthetic",
                Date = new DateTime(2024, 1, 1, 0, 0, index, DateTimeKind.Utc),
                Text = index == 1 ? new string('x', 10000)
                    : index == 16 ? "Unrelated archive fixture" : "Synthetic original message",
                Consented = true,
                Origin = index == 16 ? "legacy" : "live",
                ThreadId = index == 16 ? 7 : null,
                Forwarded = index == 16,
                UpdateId = index,
            }).ToArray();
            Assert.Equal(16, await repository.SaveBatch(records, timeout.Token));
            Assert.Equal(0, await repository.SaveBatch(records, timeout.Token));
            var activity = await repository.Query<HistoryActivity>(-1,
                new QueryDefinition("SELECT * FROM c WHERE c.kind = 'activity'"), 10, timeout.Token);
            Assert.Equal(16, Assert.Single(activity).Messages);
            var legacy = records[15];
            var priorWork = (await repository.GetWork(-1, legacy.Bucket, timeout.Token))!;
            HistoryMessage Export(int? topic = null, bool changedDate = false) => new()
            {
                ChatId = -1, SourceChatId = -1, MessageId = legacy.MessageId, UserId = 42, User = "Synthetic",
                Date = changedDate ? legacy.Date.AddSeconds(1) : legacy.Date,
                EditedAtUtc = legacy.Date.AddMinutes(1), Text = legacy.Text, Origin = "export",
                ThreadId = topic, Consented = true,
            };
            Assert.True(await repository.SaveMessage(Export(), timeout.Token));
            var promoted = (await repository.GetMessage(-1, legacy.Id, timeout.Token))!;
            Assert.Equal(7, promoted.ThreadId);
            Assert.Equal(legacy.Bucket, promoted.Bucket);
            Assert.Equal("export", promoted.Origin);
            Assert.False(promoted.Forwarded);
            Assert.Equal(priorWork.Version + 1, (await repository.GetWork(-1, legacy.Bucket, timeout.Token))!.Version);
            Assert.False(await repository.SaveMessage(Export(), timeout.Token));
            var conflictingTopic = Export(8);
            conflictingTopic.EditedAtUtc = legacy.Date.AddMinutes(2);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveMessage(conflictingTopic, timeout.Token));
            var conflictingDate = Export(changedDate: true);
            conflictingDate.EditedAtUtc = legacy.Date.AddMinutes(2);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveMessage(conflictingDate, timeout.Token));
            Assert.Equal(16, Assert.Single(await repository.Query<HistoryActivity>(-1,
                new QueryDefinition("SELECT * FROM c WHERE c.kind = 'activity'"), 10, timeout.Token)).Messages);
            var original = (await repository.GetMessage(-1, records[0].Id, timeout.Token))!;
            Assert.Equal(new string('x', 10000), original.Text);
            Assert.Null(await repository.GetMessage(-2, original.Id, timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetMessage(-3, original.Id, timeout.Token));
            var oldImport = new HistoryMessage
            {
                ChatId = -1,
                SourceChatId = -1,
                MessageId = 1,
                UserId = 42,
                User = "Synthetic",
                Date = original.Date,
                Text = "Older export",
                Origin = "export",
                Consented = true,
            };
            Assert.False(await repository.SaveMessage(oldImport, timeout.Token));
            original.Text = "Corrected current source";
            original.UpdateId = 100;
            Assert.True(await repository.SaveMessage(original, timeout.Token));
            original.Deleted = true;
            original.Text = "";
            Assert.True(await repository.SaveMessage(original, timeout.Token));
            Assert.False(await repository.SaveMessage(oldImport, timeout.Token));
            activity = await repository.Query<HistoryActivity>(-1,
                new QueryDefinition("SELECT * FROM c WHERE c.kind = 'activity'"), 10, timeout.Token);
            Assert.Equal(15, Assert.Single(activity).Messages);
            var exact = await repository.Query<System.Text.Json.JsonElement>(-1, new QueryDefinition(
                "SELECT c.yearUtc, COUNT(1) AS messages FROM c WHERE c.kind = 'message' AND c.deleted = false GROUP BY c.yearUtc"),
                10, timeout.Token);
            Assert.Equal(15, Assert.Single(exact).GetProperty("messages").GetInt32());
            var hash = HistoryHash.Of("Synthetic lease fixture");
            var checkpoint = new HistoryImport
            {
                Id = "import:" + hash,
                ArchiveHash = hash,
                ChatId = -1,
                LeaseOwner = Guid.NewGuid().ToString("N"),
                LeaseUntilUtc = DateTime.UtcNow.AddMinutes(3),
            };
            await repository.SaveImport(checkpoint, timeout.Token);
            var stale = (await repository.GetImport(-1, hash, timeout.Token))!;
            checkpoint.ProcessedEntries = 10;
            await repository.SaveImport(checkpoint, timeout.Token);
            var uncommitted = new HistoryMessage
            {
                ChatId = -1,
                SourceChatId = -1,
                MessageId = 100,
                Date = records[1].Date,
                Text = "Synthetic uncommitted record",
                Consented = true,
                UserId = 42,
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SaveBatch([uncommitted], timeout.Token, stale));
            Assert.Null(await repository.GetMessage(-1, uncommitted.Id, timeout.Token));
            Assert.Equal(10, (await repository.GetImport(-1, hash, timeout.Token))!.ProcessedEntries);
            await repository.SetConsent(-1, 42, false, timeout.Token);
            Assert.False(await repository.EnsureImportConsent(-1, 42, timeout.Token));
            Assert.False(await repository.HasConsent(-1, 42, timeout.Token));
            Assert.False(await repository.SaveMessage(uncommitted, timeout.Token));
            await repository.SetConsent(-1, 42, true, timeout.Token);
            var capture = new HistoryCapture(repository, options);
            var source = new Telegram.Bot.Types.Message
            {
                Id = 2001,
                Chat = new Telegram.Bot.Types.Chat { Id = -1 },
                From = new Telegram.Bot.Types.User { Id = 42, FirstName = "Synthetic", IsBot = false },
                Date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Text = "The synthetic observatory stores a blue telescope.",
            };
            await capture.Capture(new Telegram.Bot.Types.Update { Id = 3001, Message = source }, timeout.Token);
            var captured = (await repository.GetMessage(-1, "message:-1:2001", timeout.Token))!;
            Assert.Equal(source.Text, captured.Text);
            var anonymous = new Telegram.Bot.Types.Message
            {
                Id = 2002, Chat = source.Chat, From = source.From, Date = source.Date, Text = "Synthetic anonymous sender",
                SenderChat = new Telegram.Bot.Types.Chat { Id = -9 },
            };
            await capture.Capture(new Telegram.Bot.Types.Update { Id = 3003, Message = anonymous }, timeout.Token);
            Assert.Null(await repository.GetMessage(-1, "message:-1:2002", timeout.Token));
            var pending = (await repository.GetWork(-1, captured.Bucket, timeout.Token))!;
            var indexer = new HistoryIndexer(repository, options, new ArchiveEmbeddingClient());
            await indexer.Index(new(-1, pending.Id, pending.Version), timeout.Token);
            var indexed = (await repository.GetWork(-1, pending.Id, timeout.Token))!;
            Assert.Equal(indexed.Version, indexed.IndexedVersion);
            Assert.Single(indexed.ActiveChunks);
            var previousChunks = indexed.ActiveChunks;
            source.Text = null;
            source.Photo = new[] { new Telegram.Bot.Types.PhotoSize { FileId = "synthetic", FileUniqueId = "synthetic", Width = 1, Height = 1 } };
            source.Caption = "The synthetic observatory now stores a green telescope.";
            source.EditDate = source.Date.AddMinutes(1);
            await capture.Capture(new Telegram.Bot.Types.Update { Id = 3002, EditedMessage = source }, timeout.Token);
            captured = (await repository.GetMessage(-1, captured.Id, timeout.Token))!;
            Assert.Equal(source.Caption, captured.Text);
            pending = (await repository.GetWork(-1, captured.Bucket, timeout.Token))!;
            await indexer.Index(new(-1, pending.Id, pending.Version), timeout.Token);
            indexed = (await repository.GetWork(-1, pending.Id, timeout.Token))!;
            Assert.DoesNotContain(previousChunks[0], indexed.ActiveChunks);
            var remaining = await repository.Query<HistoryChunk>(-1,
                new QueryDefinition("SELECT * FROM c WHERE c.profile = @profile")
                    .WithParameter("@profile", ArchiveEmbeddingClient.Profile), 10, timeout.Token, chunks: true);
            Assert.Equal(indexed.ActiveChunks, remaining.Select(chunk => chunk.Id));
            Assert.Equal(512, Assert.Single(remaining).Embedding.Length);
            Assert.Null(await repository.ReserveBudget(5, timeout.Token));
            var tools = new HistoryTools(client, database.Id, options, new ArchiveEmbeddingClient());
            var reader = new Ungerfall.ChatGpt.TelegramBot.Storage.StorageContext(-1, HistoryOptions.ReaderUserId, 5000, "Count activity");
            var counted = await tools.Execute("history_query",
                """{"select":"VALUE COUNT(1)","predicate":"","groupBy":null,"orderBy":null,"limit":1,"cursor":null}""",
                reader, timeout.Token);
            Assert.Equal("ok", counted.Status);
            using (var result = System.Text.Json.JsonDocument.Parse(counted.ToJson()))
                Assert.Equal(16, result.RootElement.GetProperty("data").GetProperty("rows")[0].GetInt32());
            var datedCount = await tools.Execute("history_query",
                """{"select":"VALUE COUNT(1)","predicate":"c.userId = 42 AND c.date >= '2024-01-01' AND c.date < '2025-01-01' AND CONTAINS(c.text, 'Synthetic original message')","groupBy":null,"orderBy":null,"limit":1,"cursor":null}""",
                reader, timeout.Token);
            Assert.Equal("ok", datedCount.Status);
            using (var result = System.Text.Json.JsonDocument.Parse(datedCount.ToJson()))
                Assert.Equal(14, result.RootElement.GetProperty("data").GetProperty("rows")[0].GetInt32());
            var invalidDate = await tools.Execute("history_query",
                """{"select":"c.text","predicate":"date >= '2024-01-01'","groupBy":null,"orderBy":null,"limit":1,"cursor":null}""",
                reader, timeout.Token);
            Assert.Equal("invalid_query", invalidDate.Code);
            Assert.Contains("c.", invalidDate.Message);
            var invalidGroup = await tools.Execute("history_query",
                """{"select":"c.text","predicate":"","groupBy":"c.text","orderBy":null,"limit":1,"cursor":null}""",
                reader, timeout.Token);
            Assert.Equal("invalid_query", invalidGroup.Code);
            Assert.Contains("null for ordinary rows", invalidGroup.Message);
            var invalidPrivate = await tools.Execute("history_query",
                """{"select":"synthetic-private-content","predicate":"c.text = 'synthetic-private-content'","groupBy":null,"orderBy":null,"limit":1,"cursor":null}""",
                reader, timeout.Token);
            Assert.Equal("invalid_query", invalidPrivate.Code);
            Assert.Equal("SELECT requires permitted c.fields or COUNT(1) AS alias.", invalidPrivate.Message);
            Assert.DoesNotContain("synthetic-private-content", invalidPrivate.ToJson());
            var firstPage = await tools.Execute("history_query",
                """{"select":"c.id, c.date","predicate":"","groupBy":null,"orderBy":"c.date ASC","limit":1,"cursor":null}""",
                reader, timeout.Token);
            Assert.Equal("ok", firstPage.Status);
            using (var result = System.Text.Json.JsonDocument.Parse(firstPage.ToJson()))
            {
                var cursor = result.RootElement.GetProperty("data").GetProperty("cursor").GetString();
                Assert.NotNull(cursor);
                var continued = System.Text.Json.JsonSerializer.Serialize(new
                { select = "c.id, c.date", predicate = "", groupBy = (string?)null, orderBy = "c.date ASC", limit = 1, cursor });
                Assert.Equal("ok", (await tools.Execute("history_query", continued, reader, timeout.Token)).Status);
                Assert.Equal("invalid_query", (await tools.Execute("history_query", continued,
                    reader with { ThreadId = 7 }, timeout.Token)).Code);
            }
            Assert.Equal("forbidden", (await tools.Execute("history_activity", """{"userId":42}""",
                reader with { UserId = 99 }, timeout.Token)).Code);
            var activityResult = await tools.Execute("history_activity", """{"userId":42}""", reader, timeout.Token);
            Assert.Equal("ok", activityResult.Status);
            using (var result = System.Text.Json.JsonDocument.Parse(activityResult.ToJson()))
            {
                var years = result.RootElement.GetProperty("data").GetProperty("years").EnumerateArray().ToArray();
                Assert.Equal(2, years.Length);
                Assert.Equal(15, years[0].GetProperty("messages").GetInt32());
                Assert.Equal(1, years[1].GetProperty("messages").GetInt32());
            }
            var search = await tools.Execute("history_search", """{"query":"Which telescope is at the observatory?"}""", reader, timeout.Token);
            Assert.Equal("ok", search.Status);
            Assert.Contains("green telescope", search.ToJson());
            var settings = Ungerfall.ChatGpt.TelegramBot.Configuration.AzureOpenAiSettings.FromEnvironment();
            using var observation = new ToolCallObservationHandler();
            using var http = new HttpClient(observation);
            var quizStorage = Substitute.For<Ungerfall.ChatGpt.TelegramBot.Storage.IQuizStorage>();
            var ai = settings.CreateChatCompletionClient(settings.ChatDeployment, http,
                storageTools: new Ungerfall.ChatGpt.TelegramBot.Storage.StorageTools(quizStorage), historyTools: tools);
            const string histogramQuestion = "Draw a yearly ASCII activity histogram for Telegram user 42 for every available year. Include each exact count.";
            var histogramRequest = settings.Chat.CreateRequest(
                new[] { ChatMessage.FromSystem("Use exact history tools; do not guess counts."),
                    ChatMessage.FromUser(histogramQuestion) },
                settings.ChatDeployment) with { StorageContext = reader with { CurrentRequest = histogramQuestion } };
            var histogram = await ai.CompleteAsync(histogramRequest, timeout.Token);
            var activityCall = Assert.Single(observation.Calls, call => call.Name == "history_activity");
            Assert.Equal(42, activityCall.Arguments.GetProperty("userId").GetInt64());
            Assert.Equal(new[] { "userId" }, activityCall.Arguments.EnumerateObject().Select(property => property.Name));
            Assert.True(observation.ToolResultRequests > 0, "The model must receive the actual tool result before answering.");
            Assert.True(histogram.Successful, $"Actual history tool conversation did not finish: {histogram.ErrorType}.");
            Assert.Contains("2024", histogram.Content);
            Assert.Contains("2025", histogram.Content);
            Assert.Contains("15", histogram.Content);
            observation.Reset();
            const string searchQuestion = "What color telescope is currently stored at the observatory? Search the archived discussion for the answer.";
            var searchRequest = settings.Chat.CreateRequest(
                new[] { ChatMessage.FromSystem("Answer using relevant archived originals, not guesses."),
                    ChatMessage.FromUser(searchQuestion) }, settings.ChatDeployment)
                with { StorageContext = reader with { CurrentRequest = searchQuestion } };
            var searched = await ai.CompleteAsync(searchRequest, timeout.Token);
            Assert.Contains(observation.Calls, call => call.Name == "history_search"
                && call.Arguments.GetProperty("query").GetString()!.Contains("telescope", StringComparison.OrdinalIgnoreCase));
            Assert.True(observation.ToolResultRequests > 0);
            Assert.True(searched.Successful, $"Actual semantic tool conversation failed: {searched.ErrorType}.");
            Assert.Contains("green", searched.Content, StringComparison.OrdinalIgnoreCase);
            observation.Reset();
            const string countQuestion = "How many original messages did Telegram user 42 send in 2024 containing the exact phrase 'Synthetic original message'? Count the full matching set, not search results.";
            var countRequest = settings.Chat.CreateRequest(
                new[] { ChatMessage.FromSystem("Use complete archive counts, never a retrieval sample. For one exact count use select VALUE COUNT(1), with groupBy, orderBy, and cursor null."),
                    ChatMessage.FromUser(countQuestion) }, settings.ChatDeployment)
                with { StorageContext = reader with { CurrentRequest = countQuestion } };
            var exactCount = await ai.CompleteAsync(countRequest, timeout.Token);
            var countCalls = observation.Calls.Where(call => call.Name == "history_query").ToArray();
            Assert.InRange(countCalls.Length, 1, 3);
            var countCall = countCalls[^1];
            Assert.True(HistoryQuery.Create(countCall.Arguments, new long[] { 42 }, null).Aggregate);
            Assert.Equal("VALUE COUNT(1)", countCall.Arguments.GetProperty("select").GetString());
            Assert.Null(countCall.Arguments.GetProperty("groupBy").GetString());
            Assert.Null(countCall.Arguments.GetProperty("orderBy").GetString());
            Assert.Null(countCall.Arguments.GetProperty("cursor").GetString());
            var predicate = countCall.Arguments.GetProperty("predicate").GetString()!;
            Assert.Matches(@"c\.userId\s*=\s*42\b", predicate);
            Assert.True(
                System.Text.RegularExpressions.Regex.IsMatch(predicate, @"c\.yearUtc\s*=\s*2024\b")
                || (System.Text.RegularExpressions.Regex.IsMatch(predicate, @"c\.date\s*>=\s*'2024-01-01'")
                    && System.Text.RegularExpressions.Regex.IsMatch(predicate, @"c\.date\s*<\s*'2025-01-01'")),
                $"Synthetic count predicate: {predicate}");
            Assert.DoesNotMatch(@"\b(?:OR|NOT)\b", predicate.ToUpperInvariant());
            Assert.Matches(@"(?i:CONTAINS)\s*\(\s*c\.text\s*,\s*'Synthetic original message'\s*\)", predicate);
            Assert.DoesNotContain(observation.Calls, call => call.Name == "history_search");
            Assert.True(observation.ToolResultRequests > 0);
            Assert.True(observation.ToolResults.Any(result =>
            {
                if (result.Id != countCall.Id) return false;
                using var document = System.Text.Json.JsonDocument.Parse(result.Content);
                var root = document.RootElement;
                return root.GetProperty("status").GetString() == "ok"
                    && root.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Object
                    && data.GetProperty("complete").GetBoolean()
                    && data.GetProperty("scope").GetString() == "Currently consenting known authors in this chat/topic."
                    && data.TryGetProperty("rows", out var rows) && rows.ValueKind == System.Text.Json.JsonValueKind.Array
                    && rows.GetArrayLength() == 1 && rows[0].ValueKind == System.Text.Json.JsonValueKind.Number
                    && rows[0].GetInt32() == 14;
            }), $"Final synthetic count call did not return 14: {countCall.Arguments}");
            Assert.True(exactCount.Successful, $"Actual count tool conversation failed: {exactCount.ErrorType}.");
            Assert.True(exactCount.Content!.Contains("14", StringComparison.Ordinal)
                || exactCount.Content.Contains("fourteen", StringComparison.OrdinalIgnoreCase),
                $"Synthetic count predicate: {predicate}; answer: {exactCount.Content}");
            Assert.Equal(3, await repository.SaveBatch(new[]
            {
                new HistoryMessage { ChatId = -1, SourceChatId = -1, MessageId = 2201, UserId = 42, User = "Synthetic",
                    Date = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), Text = "boundary fixture", Consented = true },
                new HistoryMessage { ChatId = -1, SourceChatId = -1, MessageId = 2202, UserId = 42, User = "Synthetic",
                    Date = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234), Text = "boundary fixture", Consented = true },
                new HistoryMessage { ChatId = -1, SourceChatId = -1, MessageId = 2203, UserId = 42, User = "Synthetic",
                    Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234), Text = "boundary fixture", Consented = true },
            }, timeout.Token));
            var boundaries = await tools.Execute("history_query",
                """{"select":"VALUE COUNT(1)","predicate":"c.date >= '2023-01-01' AND c.date < '2024-01-01' AND CONTAINS(c.text, 'boundary fixture')","groupBy":null,"orderBy":null,"limit":1,"cursor":null}""",
                reader, timeout.Token);
            Assert.Equal("ok", boundaries.Status);
            using (var result = System.Text.Json.JsonDocument.Parse(boundaries.ToJson()))
                Assert.Equal(2, result.RootElement.GetProperty("data").GetProperty("rows")[0].GetInt32());
            await repository.EnsureImportConsent(-1, 43, timeout.Token);
            await repository.EnsureImportConsent(-1, 44, timeout.Token);
            Assert.Equal(3, await repository.SaveBatch(new[]
            {
                new HistoryMessage { ChatId = -1, SourceChatId = -1, MessageId = 2101, UserId = 42,
                    User = "Алиса", Date = new DateTime(2022, 2, 25, 0, 0, 0, DateTimeKind.Utc),
                    Text = "Привет! Как здорово! Ура! Пойдём гулять в парк. fixture-alpha", Consented = true },
                new HistoryMessage { ChatId = -1, SourceChatId = -1, MessageId = 2102, UserId = 43,
                    User = "Борис", Date = new DateTime(2022, 2, 26, 0, 0, 0, DateTimeKind.Utc),
                    Text = "Считаю, что следует рассмотреть прогулку в парке. fixture-beta", Consented = true },
                new HistoryMessage { ChatId = -1, SourceChatId = -1, MessageId = 2103, UserId = 44,
                    User = "Злата", Date = new DateTime(2022, 2, 5, 0, 0, 0, DateTimeKind.Utc),
                    Text = "Построим кактусовый дирижабль. fixture-decoy", Consented = true },
            }, timeout.Token));
            observation.Reset();
            const string portraitQuestion = "Напиши речевой портрет каждого участника чата в конце февраля 2022 года.";
            var portraitRequest = settings.Chat.CreateRequest(
                new[] { ChatMessage.FromSystem("Ты полезный ассистент в групповом чате."),
                    ChatMessage.FromUser(portraitQuestion) }, settings.ChatDeployment)
                with { StorageContext = reader with { CurrentRequest = portraitQuestion } };
            var portrait = await ai.CompleteAsync(portraitRequest, timeout.Token);
            Assert.True(observation.ToolResultRequests > 0);
            Assert.True(HasLateFebruarySources(observation),
                $"Synthetic portrait queries: {string.Join("; ", observation.Calls.Select(call => call.Arguments))}; answer: {portrait.Content}");
            Assert.True(portrait.Successful, $"Actual speech-style conversation failed: {portrait.ErrorType}.");
            Assert.Contains("Алиса", portrait.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Борис", portrait.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Злата", portrait.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("дирижаб", portrait.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"(?i)часто|обычно|натур[аыуе]|склонност|привыч", portrait.Content!);
            Assert.DoesNotContain(observation.Calls, call => call.Name.StartsWith("storage_", StringComparison.Ordinal));
            Assert.Matches(LimitedEvidence, portrait.Content);
            Assert.True(DisclosesPeriodOrUncertainty(portrait.Content!), $"Synthetic portrait answer: {portrait.Content}");
            observation.Reset();
            const string discussionQuestion = "Что обсуждали участники чата в конце февраля 2022 года?";
            var discussionRequest = settings.Chat.CreateRequest(
                new[] { ChatMessage.FromSystem("Ты полезный ассистент в групповом чате."),
                    ChatMessage.FromUser(discussionQuestion) }, settings.ChatDeployment)
                with { StorageContext = reader with { CurrentRequest = discussionQuestion } };
            var discussion = await ai.CompleteAsync(discussionRequest, timeout.Token);
            Assert.True(observation.ToolResultRequests > 0);
            Assert.True(HasLateFebruarySources(observation),
                $"Synthetic discussion queries: {string.Join("; ", observation.Calls.Select(call => call.Arguments))}; answer: {discussion.Content}");
            Assert.True(discussion.Successful, $"Actual dated-history conversation failed: {discussion.ErrorType}.");
            Assert.DoesNotContain("Злата", discussion.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("дирижаб", discussion.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"(?i)парк|прогул", discussion.Content!);
            Assert.DoesNotContain(observation.Calls, call => call.Name.StartsWith("storage_", StringComparison.Ordinal));
            Assert.Matches(LimitedEvidence, discussion.Content!);
            Assert.True(DisclosesPeriodOrUncertainty(discussion.Content!), $"Synthetic discussion answer: {discussion.Content}");
            foreach (var context in new Ungerfall.ChatGpt.TelegramBot.Storage.StorageContext?[] { reader with { UserId = 99 }, null })
            {
                observation.Reset();
                var denied = settings.Chat.CreateRequest(
                    new[] { ChatMessage.FromSystem("If archive access is unavailable, say so without guessing."),
                        ChatMessage.FromUser(histogramQuestion) }, settings.ChatDeployment)
                    with { StorageContext = context };
                await ai.CompleteAsync(denied, timeout.Token);
                Assert.Empty(observation.Calls);
                Assert.All(observation.OfferedTools, offered => Assert.Empty(offered));
                Assert.NotEmpty(observation.OfferedTools);
            }
            var oldGeneration = await repository.GetContextGeneration(-1, 42, timeout.Token);
            Assert.True(await repository.IsContextAllowed(-1, 42, oldGeneration, timeout.Token));
            await repository.SetConsent(-1, 42, false, timeout.Token, purging: true);
            var oldEpoch = (await repository.GetPurgeEpoch(-1, 42, timeout.Token))!;
            await new HistoryPrivacy(repository, options).Forget(-1, 42, timeout.Token);
            Assert.False(await repository.HasConsent(-1, 42, timeout.Token));
            Assert.Equal("forbidden", (await tools.Execute("history_activity", """{"userId":42}""", reader, timeout.Token)).Code);
            var forgotten = (await repository.GetMessage(-1, captured.Id, timeout.Token))!;
            Assert.True(forgotten.Deleted);
            Assert.Null(forgotten.UserId);
            Assert.Equal("", forgotten.Text);
            Assert.Equal("", forgotten.User);
            await repository.SetConsent(-1, 42, true, timeout.Token);
            Assert.False(await repository.IsContextAllowed(-1, 42, oldGeneration, timeout.Token));
            Assert.False(await repository.IsContextAllowed(-1, 42, null, timeout.Token));
            Assert.True(await repository.IsContextAllowed(-1, 42,
                await repository.GetContextGeneration(-1, 42, timeout.Token), timeout.Token));
            source.Id = 2004;
            source.Text = "Synthetic message after renewed consent";
            source.Photo = null;
            source.Caption = null;
            await capture.Capture(new Telegram.Bot.Types.Update { Id = 3004, Message = source }, timeout.Token);
            var staleTombstone = (await repository.GetMessage(-1, "message:-1:2004", timeout.Token))!;
            staleTombstone.Deleted = true;
            staleTombstone.Consented = false;
            staleTombstone.UserId = null;
            staleTombstone.User = "";
            staleTombstone.Text = "";
            await Assert.ThrowsAsync<HistoryPurgeSupersededException>(() =>
                repository.SaveBatch([staleTombstone], timeout.Token, purge: (42, oldEpoch)));
            await Assert.ThrowsAsync<HistoryPurgeSupersededException>(() =>
                repository.CompletePurge(-1, 42, oldEpoch, timeout.Token));
            await new HistoryPrivacy(repository, options).ResumeForget(-1, 42, timeout.Token);
            Assert.True(await repository.HasConsent(-1, 42, timeout.Token));
            Assert.Equal(source.Text, (await repository.GetMessage(-1, "message:-1:2004", timeout.Token))!.Text);
            await repository.SetConsent(-1, 42, false, timeout.Token, purging: true);
            Assert.NotEqual(oldEpoch, await repository.GetPurgeEpoch(-1, 42, timeout.Token));
            await Assert.ThrowsAsync<HistoryPurgeSupersededException>(() =>
                repository.SaveBatch([staleTombstone], timeout.Token, purge: (42, oldEpoch)));
            await Assert.ThrowsAsync<HistoryPurgeSupersededException>(() =>
                repository.CompletePurge(-1, 42, oldEpoch, timeout.Token));
            Assert.Equal(source.Text, (await repository.GetMessage(-1, "message:-1:2004", timeout.Token))!.Text);
            await Task.WhenAll(
                new HistoryPrivacy(repository, options).ResumeForget(-1, 42, timeout.Token),
                new HistoryPrivacy(new HistoryRepository(client, database.Id, options), options).ResumeForget(-1, 42, timeout.Token));
            Assert.False(await repository.HasConsent(-1, 42, timeout.Token));
            Assert.True((await repository.GetMessage(-1, "message:-1:2004", timeout.Token))!.Deleted);
        }
        catch (CosmosException error)
        {
            throw new InvalidOperationException($"Durable history live test failed: HTTP {(int)error.StatusCode}; details withheld.");
        }
        finally
        {
            if (fixture != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await fixture.DeleteContainerAsync(cancellationToken: cleanup.Token);
            }
        }
    }
    [SkippableFact]
    public async Task HistoryQueueReferenceRoundtripUsesRealServiceBusWithoutTelegram()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("DURABLE_HISTORY_LIVE_TEST") == "1",
            "Durable history live tests are not enabled.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var connection = Environment.GetEnvironmentVariable("ServiceBusConnection")!;
        var administration = new ServiceBusAdministrationClient(connection);
        var queue = "history-test-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            await administration.CreateQueueAsync(queue, timeout.Token);
            created = true;
            await using var client = new ServiceBusClient(connection);
            await using var sender = client.CreateSender(queue);
            var reference = new HistoryDispatch(-1, "work:synthetic", 2);
            await HistoryQueueClient.Send(sender, reference, timeout.Token);
            await using var receiver = client.CreateReceiver(queue);
            var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), timeout.Token);
            Assert.NotNull(received);
            Assert.Equal(reference, received.Body.ToObjectFromJson<HistoryDispatch>());
            var workerJson = new System.Text.Json.JsonSerializerOptions();
            Telegram.Bot.JsonBotAPI.Configure(workerJson);
            Assert.Equal(reference, received.Body.ToObjectFromJson<HistoryDispatch>(workerJson));
            await receiver.CompleteMessageAsync(received, timeout.Token);
        }
        catch (ServiceBusException error)
        {
            throw new InvalidOperationException($"History queue live contract failed: {error.Reason}; details withheld.");
        }
        finally
        {
            if (created)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await administration.DeleteQueueAsync(queue, cleanup.Token);
            }
        }
    }
}

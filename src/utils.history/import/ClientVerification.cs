using Microsoft.Azure.Cosmos;
using Ungerfall.ChatGpt.TelegramBot.History;

internal static class ClientVerification
{
    public static async Task Run(HistoryOptions configured)
    {
        var chat = -4000000000000000 - Random.Shared.NextInt64(1, 1000000000);
        var options = new HistoryOptions
        {
            Chats = new HashSet<long> { chat }, MessagesContainer = configured.MessagesContainer,
            ChunksContainer = configured.ChunksContainer,
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var client = new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new HistoryCosmosSerializer(), ConnectionMode = ConnectionMode.Gateway });
        var database = Environment.GetEnvironmentVariable("CosmosDatabase")!;
        var repository = new HistoryRepository(client, database, options);
        try
        {
            var original = new HistoryMessage
            {
                ChatId = chat, SourceChatId = chat, MessageId = -1, Origin = "export",
                Date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Text = "Synthetic compatibility fixture", Consented = true,
            };
            if (!await repository.SaveMessage(original, cancellation.Token)
                || (await repository.GetMessage(chat, original.Id, cancellation.Token))?.Text != original.Text)
                throw new InvalidOperationException("Production Cosmos SDK source roundtrip failed.");
            var vector = new float[512];
            vector[0] = 1;
            await repository.SaveChunk(new HistoryChunk
            {
                Id = "client-verification", ChatId = chat, Embedding = vector, MessageRefs = [original.Id],
                Revisions = [original.Revision], Starts = [0], Lengths = [original.Text.Length],
            }, cancellation.Token);
            var hits = await repository.Query<System.Text.Json.JsonElement>(chat,
                new QueryDefinition("SELECT TOP 1 c.id FROM c ORDER BY VectorDistance(c.embedding, @vector)")
                    .WithParameter("@vector", vector), 1, cancellation.Token, chunks: true);
            if (hits.Count != 1 || hits[0].GetProperty("id").GetString() != "client-verification")
                throw new InvalidOperationException("Production Cosmos SDK vector query failed.");
            Console.WriteLine($"Actual application Cosmos SDK {typeof(CosmosClient).Assembly.GetName().Version}: source roundtrip and vector SQL passed.");
        }
        catch (CosmosException error)
        {
            throw new InvalidOperationException($"Production client verification failed: HTTP {(int)error.StatusCode}; details withheld.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await repository.DeleteChunk(chat, "client-verification", cleanup.Token);
            var ids = await repository.Query<string>(chat,
                new QueryDefinition("SELECT VALUE c.id FROM c"), 20, cleanup.Token);
            foreach (var id in ids)
                await client.GetContainer(database, options.MessagesContainer).DeleteItemAsync<object>(
                    id, new PartitionKey(chat), cancellationToken: cleanup.Token);
        }
    }
}

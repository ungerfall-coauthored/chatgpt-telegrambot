using System.Security.Cryptography;
using Microsoft.Azure.Cosmos;
using SharpToken;
using Ungerfall.ChatGpt.TelegramBot.History;

var options = HistoryOptions.FromEnvironment();
if (args.SequenceEqual(["--verify-client"]))
{
    await ClientVerification.Run(options);
    return;
}
if (options.Chats.Count != 1) throw new InvalidOperationException("The importer requires one explicitly configured target chat.");
var chat = options.Chats.Single();
var file = Environment.GetEnvironmentVariable("HISTORY_EXPORT")
    ?? throw new InvalidOperationException("Set HISTORY_EXPORT privately to the local ZIP path.");
var dry = args.Contains("--dry-run");
if (!dry && !args.Contains("--confirm-consent"))
    throw new InvalidOperationException("Import requires explicit chat-scoped consent confirmation.");
using var stream = File.OpenRead(file);
var hash = Convert.ToHexString(SHA256.HashData(stream));
stream.Position = 0;
using var reader = new TelegramHistoryExport(stream);
using var timeout = new CancellationTokenSource(TimeSpan.FromHours(3));
var encoding = GptEncoding.GetEncoding("cl100k_base");
using var client = dry ? null : new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnectionString"),
    new CosmosClientOptions
    {
        Serializer = new HistoryCosmosSerializer(), ConnectionMode = ConnectionMode.Gateway,
        MaxRetryAttemptsOnRateLimitedRequests = 3, RequestTimeout = TimeSpan.FromSeconds(30),
    });
var repository = client == null ? null : new HistoryRepository(client,
    Environment.GetEnvironmentVariable("CosmosDatabase")!, options);
var manifest = repository == null ? null : await repository.GetImport(chat, hash, timeout.Token);
if (manifest?.Completed == true)
{
    Console.WriteLine("This export was already imported completely.");
    return;
}
manifest ??= new HistoryImport { Id = "import:" + hash, ChatId = chat, ArchiveHash = hash };
if (repository != null)
{
    if (manifest.LeaseUntilUtc > DateTime.UtcNow)
        throw new InvalidOperationException("Another importer holds an active lease.");
    manifest.LeaseOwner = Guid.NewGuid().ToString("N");
    manifest.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(3);
    await repository.SaveImport(manifest, timeout.Token);
}
var resume = manifest.ProcessedEntries;
var batch = new List<HistoryMessage>();
var consented = new Dictionary<long, bool>();
var previousRu = manifest.ObservedRu;
long tokens = 0, characters = 0;
var count = 0;
async Task Flush()
{
    if (repository == null || batch.Count == 0) return;
    foreach (var user in batch.Select(message => message.UserId).Where(user => user != null).Select(user => user!.Value).Distinct())
    {
        if (!consented.ContainsKey(user))
        {
            consented[user] = await repository.EnsureImportConsent(chat, user, timeout.Token);
        }
    }
    foreach (var user in batch.Select(message => message.UserId).Where(user => user != null).Select(user => user!.Value).Distinct())
        consented[user] = await repository.HasConsent(chat, user, timeout.Token);
    var allowed = batch.Where(message => message.UserId == null || consented[message.UserId.Value]).ToArray();
    manifest.ProcessedEntries = reader.EntriesRead;
    manifest.ObservedRu = previousRu + repository.RequestUnits;
    manifest.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(3);
    if (allowed.Length > 0) await repository.SaveBatch(allowed, timeout.Token, manifest);
    else await repository.SaveImport(manifest, timeout.Token);
    batch.Clear();
    if ((decimal)manifest.ObservedRu * 0.305m / 1000000 > options.ImportBudgetUsd)
        throw new InvalidOperationException("Import observed RU budget reached; checkpoint saved. Resume only after budget review.");
    if (manifest.ProcessedEntries % 4096 == 0)
        Console.WriteLine($"Import progress: entries={manifest.ProcessedEntries}; observedRU={repository.RequestUnits:F2}.");
}
try
{
    if (!dry && (decimal)previousRu * 0.305m / 1000000 > options.ImportBudgetUsd)
        throw new InvalidOperationException("Import budget was already reached; review it before resuming.");
    while (reader.ReadNext(out var entry))
    {
        if (reader.ChatId != chat) throw new InvalidOperationException("Export chat differs from the configured target.");
        if (!dry && reader.EntriesRead <= resume) continue;
        var message = TelegramHistoryExport.Message(entry, chat);
        if (message == null) continue;
        count++;
        characters += message.Text.Length;
        tokens += encoding.CountTokens(message.Text);
        manifest.Messages++;
        manifest.EarliestUtc = manifest.EarliestUtc == null || message.Date < manifest.EarliestUtc ? message.Date : manifest.EarliestUtc;
        manifest.LatestUtc = manifest.LatestUtc == null || message.Date > manifest.LatestUtc ? message.Date : manifest.LatestUtc;
        if (repository != null)
        {
            batch.Add(message);
            if (batch.Count == 16) await Flush();
        }
    }
    if (reader.ChatId != chat) throw new InvalidOperationException("Export chat differs from the configured target.");
    await Flush();
    if (repository != null)
    {
        manifest.ProcessedEntries = reader.EntriesRead;
        manifest.Completed = true;
        manifest.ObservedRu = previousRu + repository.RequestUnits;
        manifest.LeaseUntilUtc = DateTime.UtcNow.AddMinutes(3);
        await repository.SaveImport(manifest, timeout.Token);
    }
    Console.WriteLine($"History {(dry ? "dry run" : "source import")} complete: messages={count}; characters={characters}; sourceTokens={tokens}; observedRU={repository?.RequestUnits ?? 0:F2}. Source text/identities omitted.");
}
catch (CosmosException error)
{
    throw new InvalidOperationException($"History import failed: HTTP {(int)error.StatusCode}; provider details withheld.");
}

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI.Managers;
using System.Net.Http.Headers;
using Telegram.Bot;
using Ungerfall.ChatGpt.TelegramBot;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;

try
{
    var version = Required("GITHUB_SHA");
    if (Required("GITHUB_ACTIONS") != "true"
        || Required("GITHUB_REPOSITORY") != "ungerfall-coauthored/chatgpt-telegrambot"
        || Required("GITHUB_REF") != "refs/heads/master"
        || !ReleaseAnnouncementSettings.IsCommit(version)
        || version != ReleaseAnnouncementSettings.ArtifactVersion)
        throw new InvalidOperationException("Announcer must match the health-verified fork master artifact.");
    var settings = new ReleaseAnnouncementSettings(enabled: true, version: version);
    using var cosmos = new CosmosClient(Required("CosmosDbConnectionString"), new CosmosClientOptions
    {
        MaxRetryAttemptsOnRateLimitedRequests = 3,
        Serializer = new CosmosSystemTextJsonSerializer(),
    });
    var repository = new ReleaseAnnouncementRepository(cosmos, Options.Create(new CosmosDbOptions
    {
        DatabaseId = Required("CosmosDatabase"),
        TimedTasksContainerId = Required("CosmosTimedTasksContainer"),
    }));
    using var github = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
    github.DefaultRequestHeaders.UserAgent.ParseAdd("coauthored-telegrambot-release-notes");
    github.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    github.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    github.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Required("GITHUB_TOKEN"));
    var azure = AzureOpenAiSettings.FromEnvironment();
    using var summary = new SummaryOpenAiClient(new OpenAIService(azure.CreateOptions(azure.SummaryDeployment)),
        azure.SummaryDeployment, azure.Summary);
    using var telegram = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var bot = new TelegramBotClient(Required("TELEGRAM_BOT_TOKEN"), telegram);
    var announcements = new ReleaseAnnouncementService(settings, new Whitelist(), new TestUsers(), repository,
        new GitHubReleaseNotesSource(github, settings), summary, bot, new ReleaseLogger());
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    await repository.ApproveDeployment(version, timeout.Token);
    for (var attempt = 1; attempt <= 3; attempt++)
    {
        if (await announcements.Execute(timeout.Token))
        {
            Console.WriteLine($"Release announcement delivery confirmed for every configured group at {version}.");
            return 0;
        }
        Console.Error.WriteLine($"Release announcement attempt {attempt}/3 did not confirm every delivery.");
        if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(10), timeout.Token);
    }
    Console.Error.WriteLine("Release announcement failed; inspect receipts before recovering uncertain sends.");
    return 1;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Release announcement failed ({error.GetType().Name}); credentials and request details suppressed.");
    return 1;
}

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Required release configuration is missing.");

sealed class ReleaseLogger : ILogger<ReleaseAnnouncementService>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Console.Error.WriteLine(formatter(state, null));
}

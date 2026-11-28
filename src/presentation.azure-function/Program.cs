using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using Telegram.Bot;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.AzureFunction;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;
using Ungerfall.ChatGpt.TelegramBot.History;

var host = AzureFunctionHost.HostBuilder.Value;
host.Run();

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction
{
    public static class AzureFunctionHost
    {
        public static Lazy<IHost> HostBuilder { get; } = new(CreateHost, isThreadSafe: true);

        private static IHost CreateHost()
        {
            var tgToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN", EnvironmentVariableTarget.Process)
                ?? throw new ArgumentException("TELEGRAM_BOT_TOKEN is missing");

            return new HostBuilder()
               .ConfigureFunctionsWorkerDefaults()
               .ConfigureServices(s =>
               {
                   s.AddPrompts();
                   s.Configure<System.Text.Json.JsonSerializerOptions>(JsonBotAPI.Configure);
                   s.AddTelegramBotClient("tgclient", _ => tgToken);
                   s.AddAzureOpenAi();
                   s.AddAzureDecisions();
                   s.Configure<CosmosDbOptions>(opt =>
                   {
                       opt.DatabaseId = Environment.GetEnvironmentVariable(
                           "CosmosDatabase",
                           EnvironmentVariableTarget.Process)
                           ?? throw new ArgumentException("CosmosDatabase is missing");
                       opt.MessagesContainerId = Environment.GetEnvironmentVariable(
                           "CosmosTelegramMessagesContainer",
                           EnvironmentVariableTarget.Process)
                           ?? throw new ArgumentException("CosmosTelegramMessagesContainer is missing");
                       opt.ConnectionString = Environment.GetEnvironmentVariable(
                           "CosmosDbConnectionString",
                           EnvironmentVariableTarget.Process)
                           ?? throw new ArgumentException("CosmosDbConnectionString is missing");
                       opt.TimedTasksContainerId = Environment.GetEnvironmentVariable(
                           "CosmosTimedTasksContainer",
                           EnvironmentVariableTarget.Process)
                           ?? throw new ArgumentException("CosmosTimedTasksContainer is missing");
                       opt.MessageRetentionDays = int.Parse(Environment.GetEnvironmentVariable("MessageRetentionDays") ?? "1",
                           System.Globalization.CultureInfo.InvariantCulture);
                   });
                   s.AddAzureClients(c => c.AddServiceBusClient(Environment.GetEnvironmentVariable(
                       "ServiceBusConnection",
                       EnvironmentVariableTarget.Process)));
                   s.AddSingleton(sp =>
                   {
                       var opt = sp.GetRequiredService<IOptions<CosmosDbOptions>>().Value;
                       return new CosmosClient(
                           opt.ConnectionString,
                           clientOptions: new CosmosClientOptions
                           {
                               MaxRetryAttemptsOnRateLimitedRequests = 3,
                               Serializer = new CosmosSystemTextJsonSerializer(),
                           });
                   });
                   s.AddScoped<ITelegramMessageRepository, TelegramMessageRepository>();
                   s.AddSingleton(_ => HistoryOptions.FromEnvironment());
                   s.AddKeyedSingleton<CosmosClient>("history", (sp, _) => new CosmosClient(
                       sp.GetRequiredService<IOptions<CosmosDbOptions>>().Value.ConnectionString,
                       new CosmosClientOptions
                       {
                           Serializer = new HistoryCosmosSerializer(),
                           ConnectionMode = ConnectionMode.Gateway,
                           MaxRetryAttemptsOnRateLimitedRequests = 3,
                           RequestTimeout = TimeSpan.FromSeconds(30),
                       }));
                   s.AddScoped(sp => new HistoryRepository(sp.GetRequiredKeyedService<CosmosClient>("history"),
                       sp.GetRequiredService<IOptions<CosmosDbOptions>>().Value.DatabaseId,
                       sp.GetRequiredService<HistoryOptions>()));
                   s.AddScoped<IHistoryConsent>(sp => sp.GetRequiredService<HistoryRepository>());
                   s.AddScoped<HistoryCapture>();
                   s.AddScoped<HistoryPrivacy>();
                   s.AddSingleton<ArchiveEmbeddingClient>();
                   s.AddScoped<HistoryIndexer>();
                   s.AddSingleton(sp => new HistoryTools(sp.GetRequiredKeyedService<CosmosClient>("history"),
                       sp.GetRequiredService<IOptions<CosmosDbOptions>>().Value.DatabaseId,
                       sp.GetRequiredService<HistoryOptions>(), sp.GetRequiredService<ArchiveEmbeddingClient>()));
                   s.AddScoped<IConversationRepository, ConversationRepository>();
                   s.AddScoped<ConversationPolicy>();
                   s.AddSingleton<ConversationPolicyChats>();
                   s.AddScoped<ITimedTaskExecutionRepository, TimedTaskExecutionRepository>();
                   s.AddScoped<IBookQuizRepository, BookQuizRepository>();
                   s.AddScoped<ITokenCounter, TokenCounter>();
                   s.AddScoped<IWhitelist, Whitelist>();
                   s.AddScoped<TooLongDidNotReadToday>();
                   s.AddScoped<DailySummary>();
                   s.AddSingleton(_ => OnThisDayOptions.FromEnvironment());
                   s.AddScoped<OnThisDay>();
                   s.AddScoped<DailyQuiz>();
                   s.AddScoped<DailyBookQuiz>();
                   s.AddScoped<DailyFilmsQuiz>();
                   s.AddScoped<DailyVideoGamesQuiz>();
                   s.AddScoped<GenerateImage>();
                   s.AddScoped<PersonalSettings>();
                   s.AddScoped<UpdateHandler>();
                   s.AddSingleton<TestUsers>();
                   s.AddSingleton<QuizChats>();
                   s.AddSingleton<BookQuizChats>();
                   s.AddSingleton<DailySummaryChats>();
               })
               .Build();
        }
    }
}

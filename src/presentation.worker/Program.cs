using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;
using Telegram.Bot;
using Ungerfall.ChatGpt.TelegramBot;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Commands;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;
using Ungerfall.ChatGpt.TelegramBot.Worker;
using Ungerfall.ChatGpt.TelegramBot.Worker.Services;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        // Register Bot configuration
        services.Configure<Configuration>(opt =>
        {
            opt.TelegramBotToken = context.Configuration["TELEGRAM_BOT_TOKEN"] ?? throw new ArgumentNullException(nameof(context));
        });
        services.Configure<CosmosDbOptions>(opt =>
        {
            opt.DatabaseId = context.Configuration["CosmosDatabase"] ?? throw new ArgumentNullException(nameof(context));
            opt.MessagesContainerId = context.Configuration["CosmosTelegramMessagesContainer"] ?? throw new ArgumentNullException(nameof(context));
            opt.ConnectionString = context.Configuration["CosmosDbConnectionString"] ?? throw new ArgumentNullException(nameof(context));
            opt.TimedTasksContainerId = context.Configuration["CosmosTimedTasksContainer"] ?? throw new ArgumentNullException(nameof(context));
            opt.MessageRetentionDays = int.Parse(context.Configuration["MessageRetentionDays"] ?? "1",
                System.Globalization.CultureInfo.InvariantCulture);
        });

        services.AddHttpClient("telegram_bot_client")
                .AddTypedClient<ITelegramBotClient>((httpClient, sp) =>
                {
                    Configuration? botConfig = sp.GetRequiredService<IOptions<Configuration>>().Value;
                    TelegramBotClientOptions options = new(botConfig.TelegramBotToken);
                    return new TelegramBotClient(options, httpClient);
                });

        services.AddAzureOpenAi();
        services.AddAzureDecisions();
        services.AddReleaseAnnouncements();
        services.AddAzureClients(c => c.AddServiceBusClient(context.Configuration["ServiceBusConnection"]));
        services.AddSingleton(sp =>
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
        services.AddScoped<ITelegramMessageRepository, TelegramMessageRepository>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<ConversationPolicy>();
        services.AddSingleton<ConversationPolicyChats>();
        services.AddScoped<PollingUpdateHandler>();
        services.AddScoped<ReceiverService>();
        services.AddScoped<ITokenCounter, TokenCounter>();
        services.AddScoped<IWhitelist, Whitelist>();
        services.AddScoped<TooLongDidNotReadToday>();
        services.AddScoped<DailySummary>();
        services.AddScoped<DailyQuiz>();
        services.AddScoped<DailyFilmsQuiz>();
        services.AddScoped<DailyVideoGamesQuiz>();
        services.AddScoped<GenerateImage>();
        services.AddScoped<PersonalSettings>();
        services.AddSingleton<TestUsers>();
        services.AddScoped<UpdateHandler>();
        services.AddHostedService<PollingService>();
        services.AddHostedService<ReleaseAnnouncementsService>();
    })
    .Build();

await host.RunAsync();
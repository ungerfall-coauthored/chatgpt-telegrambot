using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using Telegram.Bot;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
public class BookQuizLiveTests
{
    [SkippableFact]
    public async Task SendsImageQuizAndPersistsProgress()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("BOOK_QUIZ_LIVE_TEST") == "1", "Book quiz live test is not enabled");
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is required");

        var chatId = long.Parse(Required("BOOK_QUIZ_TEST_CHAT"));
        var image = Required("BOOK_QUIZ_TEST_IMAGE");
        var options = Options.Create(new CosmosDbOptions
        {
            DatabaseId = Required("CosmosDatabase"),
            TimedTasksContainerId = Required("CosmosTimedTasksContainer"),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var cosmos = new CosmosClient(Required("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new CosmosSystemTextJsonSerializer() });
        var container = cosmos.GetContainer(options.Value.DatabaseId, options.Value.TimedTasksContainerId);
        var quiz = new BookQuiz
        {
            Id = BookQuiz.QuizId(1),
            ChatId = chatId,
            Sequence = 1,
            Chapter = 1,
            Section = "1.1",
            PrintedPage = 4,
            PdfPage = 48,
            Question = "Which UNIX variant included the sockets API?",
            Options = ["4.2BSD", "MS-DOS", "System III", "The original PDP-7 UNIX"],
            CorrectOptionId = 0,
            Explanation = "4.2BSD included TCP/IP and sockets.",
            ImageReference = image,
            ImageDescription = "A test image."
        };
        quiz.Validate();
        await container.CreateItemAsync(quiz, new PartitionKey(chatId), cancellationToken: timeout.Token);
        var chats = Substitute.For<BookQuizChats>();
        chats.Get().Returns(ImmutableHashSet.Create(chatId));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var bot = new TelegramBotClient(Required("IntegrationTests_TELEGRAM_BOT_TOKEN"), http);
        var task = new DailyBookQuiz(chats, new BookQuizRepository(cosmos, options), bot,
            NullLogger<DailyBookQuiz>.Instance);

        await task.Execute(timeout.Token);

        var progress = (await container.ReadItemAsync<BookQuizProgress>(
            "book-quiz-progress:" + BookQuiz.Series, new PartitionKey(chatId), cancellationToken: timeout.Token)).Resource;
        Assert.False(progress.Sending);
        Assert.Equal(2, progress.NextSequence);
        Assert.NotNull(progress.LastSentUtc);
    }
}

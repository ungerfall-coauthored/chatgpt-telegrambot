using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Immutable;
using System.Net;
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
        if (!options.Value.TimedTasksContainerId.StartsWith("tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("The live test requires a dedicated test container.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var cosmos = new CosmosClient(Required("CosmosDbConnectionString"),
            new CosmosClientOptions { Serializer = new CosmosSystemTextJsonSerializer() });
        var container = cosmos.GetContainer(options.Value.DatabaseId, options.Value.TimedTasksContainerId);
        var quiz = new BookQuiz
        {
            Id = BookQuiz.QuizId(1),
            ChatId = chatId,
            Sequence = 1,
            Chapter = 2,
            Section = "2.17",
            PrintedPage = 40,
            PdfPage = 84,
            Question = "Клиент отправил запрос, но не получил ответ. Можно ли заключить, что сервер не выполнил действие?",
            Options = ["Нет: действие могло выполниться, а ответ потеряться", "Да: сервер точно не получил запрос", "Нет: сервер точно выполнил запрос ровно один раз", "Да: обработка начинается лишь после получения ответа"],
            CorrectOptionId = 0,
            Explanation = "Сервер выполняет действие до отправки ответа; отсутствие ответа не доказывает отсутствие действия.",
            ImageReference = image,
            ImageDescription = "Схема обмена между клиентом и сервером."
        };
        quiz.Validate();
        var partition = new PartitionKey(chatId);
        var progressId = "book-quiz-progress:" + BookQuiz.Series;
        try
        {
            await container.ReadItemAsync<BookQuizProgress>(progressId, partition, cancellationToken: timeout.Token);
            throw new InvalidOperationException("Test chat already has book quiz progress; use an empty test container.");
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
        var created = await container.CreateItemAsync(quiz, partition, cancellationToken: timeout.Token);
        await BookQuizIngestion.Import(container, [quiz], [chatId], timeout.Token);
        var chats = Substitute.For<BookQuizChats>();
        chats.Get().Returns(ImmutableHashSet.Create(chatId));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var bot = new TelegramBotClient(Required("IntegrationTests_TELEGRAM_BOT_TOKEN"), http);
        var task = new DailyBookQuiz(chats, new BookQuizRepository(cosmos, options), bot,
            NullLogger<DailyBookQuiz>.Instance);

        await task.Execute(timeout.Token);

        var saved = await container.ReadItemAsync<BookQuizProgress>(progressId, partition, cancellationToken: timeout.Token);
        var progress = saved.Resource;
        Assert.False(progress.Sending);
        Assert.Equal(2, progress.NextSequence);
        Assert.NotNull(progress.LastSentUtc);
        await container.DeleteItemAsync<BookQuiz>(quiz.Id, partition,
            new ItemRequestOptions { IfMatchEtag = created.ETag }, timeout.Token);
        await container.DeleteItemAsync<BookQuizProgress>(progressId, partition,
            new ItemRequestOptions { IfMatchEtag = saved.ETag }, timeout.Token);
    }
}

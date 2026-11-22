using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class DailyBookQuizTests
{
    [Fact]
    public void PilotHasValidContiguousSourceReferences()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "linux-programming-interface-pilot.json"));
        var quizzes = JsonSerializer.Deserialize<BookQuiz[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        BookQuiz.ValidateBatch(quizzes);
        Assert.Equal(1, quizzes[0].Sequence);
        Assert.All(quizzes, quiz => Assert.Contains($"с. {quiz.PrintedPage}", quiz.AnswerExplanation()));
    }

    [Fact]
    public void RejectsSequenceGapsAndIncompleteImages()
    {
        var quiz = Example();
        var next = Example();
        next.Sequence = 3;
        Assert.Throws<ArgumentException>(() => BookQuiz.ValidateBatch([quiz, next]));
        next.Sequence = 2;
        quiz.Section = "1.3";
        next.Section = "1.2";
        Assert.Throws<ArgumentException>(() => BookQuiz.ValidateBatch([quiz, next]));
        quiz.ImageReference = "file-id";
        Assert.Throws<ArgumentException>(quiz.Validate);
        quiz.ImageReference = " ";
        quiz.ImageDescription = " ";
        Assert.Throws<ArgumentException>(quiz.Validate);
    }

    [Fact]
    public async Task SendsImageInPollAndAdvancesOnlyAfterSuccess()
    {
        var quiz = Example();
        quiz.ImageReference = "file-id";
        quiz.ImageDescription = "A diagram of the client and server.";
        var fixture = new Fixture(quiz);
        SendPollRequest? request = null;
        fixture.Bot.SendRequest<Message>(Arg.Do<SendPollRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 42 });

        await fixture.Task.Execute();

        Assert.NotNull(request);
        Assert.NotNull(request.Media);
        Assert.Contains("Глава 1", request.Description);
        Assert.Contains("client and server", request.Description);
        Assert.Contains("The Linux Programming Interface, гл. 1, с. 4", request.Explanation);
        Assert.Equal(2, fixture.Progress.NextSequence);
        Assert.False(fixture.Progress.Sending);
        Assert.NotNull(fixture.Progress.LastSentUtc);
        await fixture.Repository.Received(1).Save(fixture.Progress, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TelegramClientSerializesImageAndDescriptionInPoll()
    {
        var quiz = Example();
        quiz.ImageReference = "file-id";
        quiz.ImageDescription = "A diagram.";
        var fixture = new Fixture(quiz);
        var handler = new PollHandler();
        using var http = new HttpClient(handler);
        var bot = new TelegramBotClient("123456:abc", http);
        var chats = Substitute.For<BookQuizChats>();
        chats.Get().Returns(ImmutableHashSet.Create(-1L));
        var task = new DailyBookQuiz(chats, fixture.Repository, bot, NullLogger<DailyBookQuiz>.Instance);

        await task.Execute();

        Assert.NotNull(handler.Body);
        using var json = JsonDocument.Parse(handler.Body);
        Assert.Equal("photo", json.RootElement.GetProperty("media").GetProperty("type").GetString());
        Assert.Equal("file-id", json.RootElement.GetProperty("media").GetProperty("media").GetString());
        Assert.Contains("A diagram.", json.RootElement.GetProperty("description").GetString());
        Assert.Equal(2, fixture.Progress.NextSequence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyDefiniteRejectionReleasesTheReservation(bool rejected)
    {
        var fixture = new Fixture(Example());
        fixture.Bot.SendRequest<Message>(Arg.Any<SendPollRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Message>(rejected
                ? new ApiRequestException("Rejected", 403)
                : new HttpRequestException("Connection lost")));

        await fixture.Task.Execute();

        Assert.Equal(!rejected, fixture.Progress.Sending);
        Assert.Equal(1, fixture.Progress.NextSequence);
        await fixture.Repository.Received(rejected ? 1 : 0).Save(fixture.Progress, Arg.Any<CancellationToken>());
    }

    private static BookQuiz Example() => new()
    {
        Sequence = 1,
        Chapter = 1,
        Section = "1.1",
        PrintedPage = 4,
        PdfPage = 48,
        Question = "What is BSD?",
        Options = ["A", "B", "C", "D"],
        CorrectOptionId = 0,
        Explanation = "It is a UNIX family."
    };

    private sealed class Fixture
    {
        public readonly ITelegramBotClient Bot = Substitute.For<ITelegramBotClient>();
        public readonly IBookQuizRepository Repository = Substitute.For<IBookQuizRepository>();
        public readonly BookQuizProgress Progress = new() { ChatId = -1, Sending = true };
        public DailyBookQuiz Task { get; }

        public Fixture(BookQuiz quiz)
        {
            var chats = Substitute.For<BookQuizChats>();
            chats.Get().Returns(ImmutableHashSet.Create(-1L));
            Repository.TryBegin(-1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                .Returns(((BookQuiz Quiz, BookQuizProgress Progress)?)(quiz, Progress));
            Task = new DailyBookQuiz(chats, Repository, Bot, NullLogger<DailyBookQuiz>.Instance);
        }
    }

    private sealed class PollHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{"message_id":42,"date":0,"chat":{"id":-1,"type":"group"}}}""")
            };
        }
    }
}

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System.Net;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class BookQuizRepositoryTests
{
    [Fact]
    public async Task ClaimsAreExclusiveAndUnconfirmedDeliveryBlocksLaterQuizzes()
    {
        var store = new Store();
        var now = DateTime.UtcNow;
        var claims = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => store.Repository.TryBegin(-1, now, default))));
        var claim = Assert.Single(claims, x => x.HasValue)!.Value;
        Assert.Equal(1, claim.Quiz.Sequence);
        Assert.Null(await store.Repository.TryBegin(-1, now.AddDays(1), default));

        claim.Progress.Sending = false;
        claim.Progress.NextSequence = 2;
        claim.Progress.LastSentUtc = now;
        await store.Repository.Save(claim.Progress, default);
        Assert.Null(await store.Repository.TryBegin(-1, now, default));
        Assert.Equal(2, (await store.Repository.TryBegin(-1, now.AddDays(1), default))!.Value.Quiz.Sequence);
        Assert.Equal(1, (await store.Repository.TryBegin(-2, now, default))!.Value.Quiz.Sequence);
    }

    [Fact]
    public async Task MissingNextSequenceNeverSkipsAhead()
    {
        var store = new Store();
        store.MissingFirst = true;
        Assert.Null(await store.Repository.TryBegin(-1, DateTime.UtcNow, default));
        Assert.Empty(store.Progress);
    }

    private sealed class Store
    {
        private readonly object _gate = new();
        private readonly CosmosSystemTextJsonSerializer _serializer = new();
        public readonly Dictionary<long, (BookQuizProgress Document, string ETag)> Progress = [];
        public bool MissingFirst;
        public BookQuizRepository Repository { get; }

        public Store()
        {
            var container = Substitute.For<Container>();
            var cosmos = Substitute.For<CosmosClient>();
            cosmos.GetContainer("db", "tasks").Returns(container);
            Repository = new BookQuizRepository(cosmos,
                Options.Create(new CosmosDbOptions { DatabaseId = "db", TimedTasksContainerId = "tasks" }));
            container.ReadItemAsync<BookQuizProgress>(Arg.Any<string>(), Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                lock (_gate)
                {
                    var chatId = Chat(call.Arg<PartitionKey>());
                    if (!Progress.TryGetValue(chatId, out var item)) throw Failure(HttpStatusCode.NotFound);
                    return Task.FromResult(Response(item.Document, item.ETag));
                }
            });
            container.ReadItemAsync<BookQuiz>(Arg.Any<string>(), Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var sequence = int.Parse(call.Arg<string>().Split(':').Last());
                if (MissingFirst && sequence == 1) throw Failure(HttpStatusCode.NotFound);
                var quiz = new BookQuiz
                {
                    Id = call.Arg<string>(),
                    ChatId = Chat(call.Arg<PartitionKey>()),
                    Sequence = sequence,
                    Chapter = 1,
                    Section = "1.1",
                    PrintedPage = 4,
                    PdfPage = 48,
                    Question = "A question?",
                    Options = ["A", "B", "C", "D"],
                    CorrectOptionId = 0,
                    Explanation = "An explanation."
                };
                var response = Substitute.For<ItemResponse<BookQuiz>>();
                response.Resource.Returns(quiz);
                return Task.FromResult(response);
            });
            container.CreateItemAsync(Arg.Any<BookQuizProgress>(), Arg.Any<PartitionKey?>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                lock (_gate)
                {
                    var progress = call.Arg<BookQuizProgress>();
                    if (Progress.ContainsKey(progress.ChatId)) throw Failure(HttpStatusCode.Conflict);
                    var etag = Guid.NewGuid().ToString();
                    Progress.Add(progress.ChatId, (Clone(progress), etag));
                    return Task.FromResult(Response(progress, etag));
                }
            });
            container.ReplaceItemAsync(Arg.Any<BookQuizProgress>(), Arg.Any<string>(), Arg.Any<PartitionKey?>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                lock (_gate)
                {
                    var progress = call.Arg<BookQuizProgress>();
                    if (!Progress.TryGetValue(progress.ChatId, out var current)
                        || current.ETag != call.Arg<ItemRequestOptions>().IfMatchEtag)
                        throw Failure(HttpStatusCode.PreconditionFailed);
                    var etag = Guid.NewGuid().ToString();
                    Progress[progress.ChatId] = (Clone(progress), etag);
                    return Task.FromResult(Response(progress, etag));
                }
            });
        }

        private static long Chat(PartitionKey key) =>
            (long)decimal.Parse(key.ToString().Trim('[', ']'), System.Globalization.CultureInfo.InvariantCulture);
        private BookQuizProgress Clone(BookQuizProgress value) =>
            _serializer.FromStream<BookQuizProgress>(_serializer.ToStream(value));
        private ItemResponse<BookQuizProgress> Response(BookQuizProgress value, string etag)
        {
            var response = Substitute.For<ItemResponse<BookQuizProgress>>();
            response.Resource.Returns(Clone(value));
            response.ETag.Returns(etag);
            return response;
        }
        private static CosmosException Failure(HttpStatusCode status) => new("Simulated storage response", status, 0, "test", 0);
    }
}

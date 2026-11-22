using Microsoft.Azure.Cosmos;
using System.Net;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class BookQuizIngestionTests
{
    [Fact]
    public async Task ReimportIsIdempotentButNeverOverwritesDifferentContent()
    {
        var container = Substitute.For<Container>();
        var serializer = new CosmosSystemTextJsonSerializer();
        var documents = new Dictionary<(string, string), BookQuiz>();
        container.CreateItemAsync(Arg.Any<BookQuiz>(), Arg.Any<PartitionKey?>(),
            Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var item = call.Arg<BookQuiz>();
            var key = (item.Id, call.Arg<PartitionKey?>()!.Value.ToString());
            if (documents.ContainsKey(key)) throw new CosmosException("exists", HttpStatusCode.Conflict, 0, "test", 0);
            documents[key] = serializer.FromStream<BookQuiz>(serializer.ToStream(item));
            return Task.FromResult(Substitute.For<ItemResponse<BookQuiz>>());
        });
        container.ReadItemAsync<BookQuiz>(Arg.Any<string>(), Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var response = Substitute.For<ItemResponse<BookQuiz>>();
            if (!documents.TryGetValue((call.Arg<string>(), call.Arg<PartitionKey>().ToString()), out var document))
                throw new CosmosException("missing", HttpStatusCode.NotFound, 0, "test", 0);
            response.Resource.Returns(document);
            return Task.FromResult(response);
        });
        var quiz = new BookQuiz
        {
            Sequence = 1,
            Chapter = 1,
            Section = "1.1",
            PrintedPage = 4,
            PdfPage = 48,
            Question = "Question?",
            Options = ["A", "B", "C", "D"],
            CorrectOptionId = 0,
            Explanation = "Explanation."
        };
        await BookQuizIngestion.Import(container, [quiz], [-1, -2, -1], default);
        await BookQuizIngestion.Import(container, [quiz], [-1, -2], default);
        Assert.Equal(2, documents.Count);
        quiz.Question = "Changed question?";
        await Assert.ThrowsAsync<InvalidOperationException>(() => BookQuizIngestion.Import(container, [quiz], [-1], default));
        Assert.All(documents.Values, item => Assert.Equal("Question?", item.Question));
    }

    [Fact]
    public async Task LaterBatchRequiresAnOrderedPredecessor()
    {
        var container = Substitute.For<Container>();
        var previous = new BookQuiz
        {
            Sequence = 1,
            Chapter = 1,
            Section = "1.3",
            PrintedPage = 12,
            PdfPage = 56,
            Question = "Previous?",
            Options = ["A", "B", "C", "D"],
            Explanation = "Explanation."
        };
        var next = new BookQuiz
        {
            Sequence = 2,
            Chapter = 1,
            Section = "1.2",
            PrintedPage = 4,
            PdfPage = 48,
            Question = "Next?",
            Options = ["A", "B", "C", "D"],
            Explanation = "Explanation."
        };
        var response = Substitute.For<ItemResponse<BookQuiz>>();
        response.Resource.Returns(previous);
        container.ReadItemAsync<BookQuiz>(BookQuiz.QuizId(1), Arg.Any<PartitionKey>(),
            Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>()).Returns(response);
        await Assert.ThrowsAsync<ArgumentException>(() => BookQuizIngestion.Import(container, [next], [-1], default));
        await container.DidNotReceive().CreateItemAsync(Arg.Any<BookQuiz>(), Arg.Any<PartitionKey?>(),
            Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>());
    }
}

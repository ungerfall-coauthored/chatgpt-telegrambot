using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;

namespace Ungerfall.ChatGpt.TelegramBot.Database;

public sealed class BookQuizRepository(CosmosClient cosmos, IOptions<CosmosDbOptions> options) : IBookQuizRepository
{
    private readonly Container _container = cosmos.GetContainer(options.Value.DatabaseId, options.Value.TimedTasksContainerId);

    public async Task<(BookQuiz Quiz, BookQuizProgress Progress)?> TryBegin(long chatId, DateTime now, CancellationToken cancellation)
    {
        var partition = new PartitionKey(chatId);
        BookQuizProgress progress;
        try
        {
            var result = await _container.ReadItemAsync<BookQuizProgress>(
                "book-quiz-progress:" + BookQuiz.Series, partition, cancellationToken: cancellation);
            progress = result.Resource;
            progress.ETag = result.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            progress = new BookQuizProgress { ChatId = chatId };
        }

        if (progress.Sending || progress.LastSentUtc?.Date == now.Date) return null;
        BookQuiz quiz;
        try
        {
            quiz = (await _container.ReadItemAsync<BookQuiz>(
                BookQuiz.QuizId(progress.NextSequence), partition, cancellationToken: cancellation)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        quiz.Validate();
        if (quiz.Sequence != progress.NextSequence || quiz.ChatId != chatId)
            throw new InvalidOperationException("Book quiz does not match progress.");
        progress.Sending = true;
        try { await Save(progress, cancellation); }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
        return (quiz, progress);
    }

    public async Task Save(BookQuizProgress progress, CancellationToken cancellation)
    {
        var partition = new PartitionKey(progress.ChatId);
        var result = progress.ETag == null
            ? await _container.CreateItemAsync(progress, partition, cancellationToken: cancellation)
            : await _container.ReplaceItemAsync(progress, progress.Id, partition,
                new ItemRequestOptions { IfMatchEtag = progress.ETag }, cancellation);
        progress.ETag = result.ETag;
    }
}
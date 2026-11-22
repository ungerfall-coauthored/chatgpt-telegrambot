using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.SourceGenerators;

namespace Ungerfall.ChatGpt.TelegramBot.Database;

public static class BookQuizIngestion
{
    public static async Task Import(Container container, BookQuiz[] quizzes, IEnumerable<long> chats, CancellationToken cancellation)
    {
        BookQuiz.ValidateBatch(quizzes);
        foreach (var chatId in chats.Distinct())
        {
            var partition = new PartitionKey(chatId);
            if (quizzes[0].Sequence > 1)
            {
                try
                {
                    var previous = (await container.ReadItemAsync<BookQuiz>(
                        BookQuiz.QuizId(quizzes[0].Sequence - 1), partition, cancellationToken: cancellation)).Resource;
                    BookQuiz.ValidateBatch([previous, quizzes[0]]);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException($"Previous quiz {quizzes[0].Sequence - 1} is missing for chat {chatId}.");
                }
            }
            foreach (var quiz in quizzes)
            {
                quiz.ChatId = chatId;
                quiz.Id = BookQuiz.QuizId(quiz.Sequence);
                try
                {
                    await container.CreateItemAsync(quiz, partition, cancellationToken: cancellation);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    var existing = (await container.ReadItemAsync<BookQuiz>(quiz.Id, partition, cancellationToken: cancellation)).Resource;
                    if (JsonSerializer.Serialize(existing, CosmosContext.Default.BookQuiz)
                        != JsonSerializer.Serialize(quiz, CosmosContext.Default.BookQuiz))
                        throw new InvalidOperationException($"Existing quiz {quiz.Sequence} differs; it was not overwritten.");
                }
            }
        }
    }
}
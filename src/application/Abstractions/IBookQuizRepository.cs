using System;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Abstractions;

public interface IBookQuizRepository
{
    Task<(BookQuiz Quiz, BookQuizProgress Progress)?> TryBegin(long chatId, DateTime now, CancellationToken cancellation);
    Task Save(BookQuizProgress progress, CancellationToken cancellation);
}
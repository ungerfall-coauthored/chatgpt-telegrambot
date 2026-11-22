using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.TimedTasks;

public sealed class DailyBookQuiz(BookQuizChats chats, IBookQuizRepository repo, ITelegramBotClient bot, ILogger<DailyBookQuiz> logger)
{
    public const string CRON_EXPRESSION = "0 0 6 * * *";

    public async Task Execute(CancellationToken cancellation = default)
    {
        foreach (var chatId in chats.Get())
        {
            try
            {
                var now = DateTime.UtcNow;
                var reservation = await repo.TryBegin(chatId, now, cancellation);
                if (reservation is not { } item) continue;
                var quiz = item.Quiz;
                var description = $"{BookQuiz.BookTitle} · Глава {quiz.Chapter} · Вопрос {quiz.Sequence}";
                if (quiz.ImageDescription != null) description += $"\n{quiz.ImageDescription}";
                try
                {
                    await bot.SendPoll(chatId, quiz.Question,
                        Array.ConvertAll(quiz.Options, option => new InputPollOption(option)),
                        type: PollType.Quiz, correctOptionIds: new[] { quiz.CorrectOptionId },
                        explanation: quiz.AnswerExplanation(), explanationParseMode: ParseMode.None,
                        description: description,
                        media: quiz.ImageReference == null ? null : new InputMediaPhoto(InputFile.FromString(quiz.ImageReference)),
                        cancellationToken: cancellation);
                }
                catch (ApiRequestException ex) when (TelegramDeliveryRejectedException.IsDefiniteRejection(ex))
                {
                    item.Progress.Sending = false;
                    await repo.Save(item.Progress, CancellationToken.None);
                    throw;
                }
                item.Progress.Sending = false;
                item.Progress.NextSequence++;
                item.Progress.LastSentUtc = DateTime.UtcNow;
                await repo.Save(item.Progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Book quiz failed for {ChatId}; unconfirmed deliveries require manual resolution", chatId);
                if (ex is OperationCanceledException && cancellation.IsCancellationRequested) throw;
            }
        }
    }
}
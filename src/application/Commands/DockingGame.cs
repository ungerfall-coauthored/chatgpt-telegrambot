using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Ungerfall.ChatGpt.TelegramBot.Commands;

public sealed class DockingGame
{
    public const string GameUrl = "https://ungerfall-coauthored.github.io/docking-game/";
    public const string ShortName = "docking";
    private readonly ITelegramBotClient _botClient;

    public DockingGame(ITelegramBotClient botClient)
    {
        _botClient = botClient;
    }

    public bool Matches(string? shortName) => string.Equals(ShortName, shortName, StringComparison.Ordinal);

    public async Task Execute(Message message, Func<Task> beforeSend, CancellationToken cancellation)
    {
        await beforeSend();
        try
        {
            await _botClient.SendGame(message.Chat.Id, ShortName, replyParameters: message.Id,
                messageThreadId: message.MessageThreadId, cancellationToken: cancellation);
        }
        catch (ApiRequestException ex) when (TelegramDeliveryRejectedException.IsDefiniteRejection(ex))
        {
            throw new TelegramDeliveryRejectedException(ex);
        }
        catch (RequestException ex) when (ex is not ApiRequestException)
        {
            throw new TelegramDeliveryUnknownException(ex);
        }
    }

    public async Task Answer(CallbackQuery query, bool allowed, CancellationToken cancellation)
    {
        await _botClient.AnswerCallbackQuery(query.Id,
            text: allowed ? null : "Игра недоступна в этом чате.",
            showAlert: !allowed, url: allowed ? GameUrl : null, cancellationToken: cancellation);
    }
}

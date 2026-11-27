using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryCapture(HistoryRepository repository, HistoryOptions options)
{
    public async Task Capture(Update update, CancellationToken cancellation)
    {
        var before = repository.RequestUnits;
        var source = update.Message ?? update.EditedMessage;
        if (source == null || !options.Allows(source.Chat.Id) || source.Id <= 0
            || source.From is not { IsBot: false } author)
            return;
        var type = source.Type.ToString().ToLowerInvariant();
        if (type is not ("text" or "photo" or "video" or "animation" or "audio" or "voice" or "videonote"
            or "document" or "sticker" or "contact" or "location" or "venue" or "poll" or "dice" or "paidmedia" or "story"))
            return;
        if (!await repository.HasConsent(source.Chat.Id, author.Id, cancellation)) return;
        var record = new HistoryMessage
        {
            ChatId = source.Chat.Id, SourceChatId = source.Chat.Id, MessageId = source.Id,
            UserId = author.Id, User = (author.FirstName + " " + author.LastName).Trim(),
            Text = source.Text ?? source.Caption ?? "",
            MessageType = type,
            Date = source.Date.ToUniversalTime(), EditedAtUtc = source.EditDate?.ToUniversalTime(),
            ThreadId = source.MessageThreadId, ReplyToMessageId = source.ReplyToMessage?.Id,
            Origin = "live", UpdateId = update.Id, Consented = true, Forwarded = source.ForwardOrigin != null,
        };
        await repository.SaveMessage(record, cancellation);
        await repository.SettleBudget(null, HistoryRepository.RuCost(repository.RequestUnits - before + 7), cancellation);
    }
}

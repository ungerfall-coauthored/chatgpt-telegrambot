using Telegram.Bot;
using Telegram.Bot.Types.Enums;

internal static class TelegramVerification
{
    public static async Task Run(long chat)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
            ?? throw new InvalidOperationException("Privately load the configured Telegram credential.");
        var bot = new TelegramBotClient(token, http);
        try
        {
            var identity = await bot.GetMe(timeout.Token);
            var webhook = await bot.GetWebhookInfo(timeout.Token);
            var membership = await bot.GetChatMember(chat, identity.Id, timeout.Token);
            if (identity.CanReadAllGroupMessages != true
                && membership.Status is not (ChatMemberStatus.Administrator or ChatMemberStatus.Creator))
                throw new InvalidOperationException("Hosted bot visibility is insufficient for full live history capture.");
            if (string.IsNullOrWhiteSpace(webhook.Url)
                || webhook.AllowedUpdates is { Length: > 0 } updates
                    && (!updates.Contains(UpdateType.Message) || !updates.Contains(UpdateType.EditedMessage)))
                throw new InvalidOperationException("Webhook must be active and include original messages plus edits.");
            Console.WriteLine("Actual Telegram client verified hosted bot visibility and active webhook message/edit coverage; identities omitted.");
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException error)
        {
            throw new InvalidOperationException($"Telegram prerequisite check failed: HTTP {error.ErrorCode}; provider details withheld.");
        }
    }
}

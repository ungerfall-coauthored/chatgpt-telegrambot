using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using Ungerfall.ChatGpt.TelegramBot.History;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;
public class QueuedUpdateHandler
{
    private readonly UpdateHandler _updateHandler;
    private readonly ILogger<QueuedUpdateHandler> _logger;
    private readonly HistoryCapture? _history;

    public QueuedUpdateHandler(ILogger<QueuedUpdateHandler> logger, UpdateHandler updateHandler, HistoryCapture? history = null)
    {
        _logger = logger;
        _updateHandler = updateHandler;
        _history = history;
    }

    [Function("QueuedUpdateHandler")]
    public async Task Run([ServiceBusTrigger(Const.TGBOT_UPDATES, Connection = "ServiceBusConnection")] Update update)
    {
        _logger.LogInformation("Telegram update received: type={UpdateType}", update.Type);
        if (_history != null) await _history.Capture(update, CancellationToken.None);
        try
        {
            await _updateHandler.Handle(update, CancellationToken.None);
        }
        catch (TelegramDeliveryUnknownException ex)
        {
            _logger.LogError(ex, "Delivery of update {UpdateId} is unconfirmed; skipping automatic retry", update.Id);
        }
        _logger.LogInformation("C# ServiceBus queue trigger function finished");
    }
}

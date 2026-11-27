using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public static class HistoryQueueClient
{
    public static Task Send(ServiceBusSender sender, HistoryDispatch payload, CancellationToken cancellation) =>
        sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromObjectAsJson(payload))
        {
            MessageId = HistoryHash.Of($"{payload.ChatId}:{payload.WorkId}:{payload.Version}"),
            TimeToLive = TimeSpan.FromDays(1),
        }, cancellation);
}

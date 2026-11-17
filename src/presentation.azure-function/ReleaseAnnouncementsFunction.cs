using Microsoft.Azure.Functions.Worker;
using System.Threading;
using System.Threading.Tasks;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;

public sealed class ReleaseAnnouncementsFunction(ReleaseAnnouncementService announcements)
{
    [Function("ReleaseAnnouncements")]
    public Task Run([TimerTrigger("0 * * * * *")] TimerInfo timer, CancellationToken cancellation) =>
        announcements.Execute(cancellation);
}
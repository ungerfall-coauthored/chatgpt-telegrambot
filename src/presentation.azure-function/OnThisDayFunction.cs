using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;

public sealed class OnThisDayFunction(OnThisDay task)
{
    [Function("OnThisDay")]
    public Task Run([TimerTrigger(OnThisDay.CRON_EXPRESSION)] TimerInfo timer) => task.Execute();
}

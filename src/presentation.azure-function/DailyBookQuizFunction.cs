using Microsoft.Azure.Functions.Worker;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.TimedTasks;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;

public sealed class DailyBookQuizFunction(DailyBookQuiz task)
{
    [Function("DailyBookQuiz")]
    public Task Run([TimerTrigger(DailyBookQuiz.CRON_EXPRESSION)] TimerInfo timer) => task.Execute();
}
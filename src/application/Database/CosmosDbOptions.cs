using System;

namespace Ungerfall.ChatGpt.TelegramBot.Database;
public class CosmosDbOptions
{
    public string ConnectionString { get; set; } = null!;
    public string DatabaseId { get; set; } = null!;
    public string MessagesContainerId { get; set; } = null!;
    public string TimedTasksContainerId { get; set; } = null!;
    public int MessageRetentionDays { get; set; } = 1;
    public int MessageTtlSeconds => MessageRetentionDays > 0
        ? checked(MessageRetentionDays * 24 * 60 * 60)
        : throw new ArgumentOutOfRangeException(nameof(MessageRetentionDays));
}

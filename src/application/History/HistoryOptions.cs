using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class HistoryOptions
{
    public bool Enabled { get; init; }
    public IReadOnlySet<long> Chats { get; init; } = new HashSet<long>();
    public string MessagesContainer { get; init; } = "historyMessages";
    public string ChunksContainer { get; init; } = "historyChunks";
    public decimal MonthlyBudgetUsd { get; init; } = 5;
    public decimal ImportBudgetUsd { get; init; } = 2;
    public const long ReaderUserId = 66215515;
    public const string QueueName = "tgbot-history";

    public bool Allows(long chatId) => Enabled && Chats.Contains(chatId);

    public static HistoryOptions FromEnvironment()
    {
        string? Get(string name) => Environment.GetEnvironmentVariable(name);
        var enabled = bool.Parse(Get("HistoryEnabled") ?? "false");
        var chats = (Get("HistoryChats") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => long.Parse(value, CultureInfo.InvariantCulture)).ToHashSet();
        if (enabled && (chats.Count == 0 || chats.Any(chat => chat >= 0)))
            throw new InvalidOperationException("History requires explicitly configured group chats.");
        var monthly = decimal.Parse(Get("HistoryMonthlyBudgetUsd") ?? "5", CultureInfo.InvariantCulture);
        var import = decimal.Parse(Get("HistoryImportBudgetUsd") ?? "2", CultureInfo.InvariantCulture);
        if (monthly <= 0 || import <= 0) throw new InvalidOperationException("History budgets must be positive.");
        return new()
        {
            Enabled = enabled, Chats = chats,
            MessagesContainer = Get("HistoryMessagesContainer") ?? "historyMessages",
            ChunksContainer = Get("HistoryChunksContainer") ?? "historyChunks",
            MonthlyBudgetUsd = monthly, ImportBudgetUsd = import,
        };
    }
}

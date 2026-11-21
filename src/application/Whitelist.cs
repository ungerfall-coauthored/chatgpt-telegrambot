using System;
using System.Collections.Generic;
using System.Linq;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot;
public class Whitelist : IWhitelist
{
    private static readonly long[] AllowedGroups = [-149593031, -1001034436662];
    private readonly PromptCatalog _prompts;

    public Whitelist(PromptCatalog? prompts = null)
    {
        _prompts = prompts ?? PromptCatalog.Load();
        if (AllowedGroups.Any(id => !_prompts.Chat.Groups.ContainsKey(id) || !_prompts.Summaries.SystemByGroup.ContainsKey(id)))
            throw new InvalidOperationException("Prompt configuration is missing an allowed group's prompt.");
    }

    public string GetSystemRoleMessage(long chatId) => _prompts.ChatSystem(chatId);
    public string GetSummarySystemRoleMessage(long chatId) => _prompts.SummarySystem(chatId);
    public bool IsGroupAllowedToUseBot(long chatId) => AllowedGroups.Contains(chatId);
    public IEnumerable<long> GetAllowedGroupIds() => AllowedGroups;
}

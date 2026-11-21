using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public sealed class PromptCatalog
{
    public ChatPrompts Chat { get; set; } = null!;
    public DecisionPrompts Decisions { get; set; } = null!;
    public SummaryPrompts Summaries { get; set; } = null!;
    public string ReleaseAnnouncement { get; set; } = null!;

    public static PromptCatalog Load() => Load(Path.Combine(AppContext.BaseDirectory, "prompts.json"));

    public static PromptCatalog Load(string path)
    {
        var catalog = JsonSerializer.Deserialize<PromptCatalog>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Prompt configuration is empty.");
        catalog.Validate();
        return catalog;
    }

    private void Validate()
    {
        static void Required(params string?[] values)
        {
            if (values.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Prompt configuration has missing or empty instructions.");
        }

        if (Chat?.Groups == null || Decisions == null || Summaries?.SystemByGroup == null)
            throw new InvalidDataException("Prompt configuration has missing sections.");
        Required(Chat.Default, Chat.UntrustedDataPrefix, Chat.Continuation, Chat.Autonomous,
            Decisions.UntrustedDataPrefix, Decisions.NaturalRequest, Decisions.Continuation,
            Decisions.Interjection, Decisions.CasualContext, Summaries.DefaultSystem, Summaries.Daily,
            Summaries.Tldr, Summaries.ShrinkSystem, Summaries.ShrinkUserPrefix, ReleaseAnnouncement);
        Required(Chat.Groups.Values.Concat(Summaries.SystemByGroup.Values).ToArray());
    }

    public string ChatSystem(long chatId) => Chat.Groups.GetValueOrDefault(chatId) ?? Chat.Default;
    public string SummarySystem(long chatId) => Summaries.SystemByGroup.GetValueOrDefault(chatId) ?? Summaries.DefaultSystem;
}

public sealed class ChatPrompts
{
    public string Default { get; set; } = null!;
    public Dictionary<long, string> Groups { get; set; } = null!;
    public string UntrustedDataPrefix { get; set; } = null!;
    public string Continuation { get; set; } = null!;
    public string Autonomous { get; set; } = null!;
}

public sealed class DecisionPrompts
{
    public string UntrustedDataPrefix { get; set; } = null!;
    public string NaturalRequest { get; set; } = null!;
    public string Continuation { get; set; } = null!;
    public string Interjection { get; set; } = null!;
    public string CasualContext { get; set; } = null!;
}

public sealed class SummaryPrompts
{
    public string DefaultSystem { get; set; } = null!;
    public Dictionary<long, string> SystemByGroup { get; set; } = null!;
    public string Daily { get; set; } = null!;
    public string Tldr { get; set; } = null!;
    public string ShrinkSystem { get; set; } = null!;
    public string ShrinkUserPrefix { get; set; } = null!;
}

public static class PromptServiceCollectionExtensions
{
    public static IServiceCollection AddPrompts(this IServiceCollection services)
    {
        var catalog = PromptCatalog.Load();
        _ = new Whitelist(catalog);
        return services.AddSingleton(catalog);
    }
}

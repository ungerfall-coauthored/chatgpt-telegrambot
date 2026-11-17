using System;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public sealed class ReleaseAnnouncementSettings
{
    public ReleaseAnnouncementSettings(
        bool enabled = false, string? version = null,
        string repository = "ungerfall-coauthored/chatgpt-telegrambot", int maxCommits = 50, int retryDelayMinutes = 15)
    {
        if (enabled && !IsCommit(version))
            throw new ArgumentException("ReleaseVersion must be the full deployed Git commit SHA.");
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("ReleaseRepository must be owner/repository.");
        if (maxCommits is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxCommits));
        if (retryDelayMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(retryDelayMinutes));
        Enabled = enabled;
        Version = version?.ToLowerInvariant() ?? string.Empty;
        Repository = repository;
        MaxCommits = maxCommits;
        RetryDelay = TimeSpan.FromMinutes(retryDelayMinutes);
    }

    public bool Enabled { get; }
    public string Version { get; }
    public string Repository { get; }
    public int MaxCommits { get; }
    public TimeSpan RetryDelay { get; }

    public static bool IsCommit(string? value) =>
        value != null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{40}\z");

    public static ReleaseAnnouncementSettings FromEnvironment()
    {
        var information = typeof(ReleaseAnnouncementSettings).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = Environment.GetEnvironmentVariable("ReleaseVersion")
            ?? (information?.Split('+', 2) is { Length: 2 } parts ? parts[1] : null);
        return new ReleaseAnnouncementSettings(
            bool.Parse(Environment.GetEnvironmentVariable("ReleaseAnnouncementsEnabled") ?? "false"),
            version, Environment.GetEnvironmentVariable("ReleaseRepository") ?? "ungerfall-coauthored/chatgpt-telegrambot",
            int.Parse(Environment.GetEnvironmentVariable("ReleaseNotesMaxCommits") ?? "50", CultureInfo.InvariantCulture),
            int.Parse(Environment.GetEnvironmentVariable("ReleaseRetryDelayMinutes") ?? "15", CultureInfo.InvariantCulture));
    }
}
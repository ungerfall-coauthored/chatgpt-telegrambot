using System;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public sealed class ReleaseAnnouncementSettings
{
    public ReleaseAnnouncementSettings(
        bool enabled = false, string? version = null,
        string repository = "ungerfall-coauthored/chatgpt-telegrambot", int maxCommits = 50,
        bool requireDeploymentApproval = true)
    {
        if (enabled && !IsCommit(version))
            throw new ArgumentException("ReleaseVersion must be the full deployed Git commit SHA.");
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new ArgumentException("ReleaseRepository must be owner/repository.");
        if (maxCommits is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxCommits));
        Enabled = enabled;
        Version = version?.ToLowerInvariant() ?? string.Empty;
        Repository = repository;
        MaxCommits = maxCommits;
        RequireDeploymentApproval = requireDeploymentApproval;
    }

    public bool Enabled { get; }
    public string Version { get; }
    public string Repository { get; }
    public int MaxCommits { get; }
    public bool RequireDeploymentApproval { get; }

    public static string ArtifactVersion =>
        typeof(ReleaseAnnouncementSettings).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)
            is { Length: 2 } parts ? parts[1] : string.Empty;

    public static bool IsCommit(string? value) =>
        value != null && Regex.IsMatch(value, @"\A[0-9a-fA-F]{40}\z");

    public static ReleaseAnnouncementSettings FromEnvironment()
    {
        var version = Environment.GetEnvironmentVariable("ReleaseVersion")
            ?? ArtifactVersion;
        return new ReleaseAnnouncementSettings(
            bool.Parse(Environment.GetEnvironmentVariable("ReleaseAnnouncementsEnabled") ?? "false"),
            version, Environment.GetEnvironmentVariable("ReleaseRepository") ?? "ungerfall-coauthored/chatgpt-telegrambot",
            int.Parse(Environment.GetEnvironmentVariable("ReleaseNotesMaxCommits") ?? "50", CultureInfo.InvariantCulture),
            bool.Parse(Environment.GetEnvironmentVariable("ReleaseAnnouncementsRequireApproval") ?? "true"));
    }
}
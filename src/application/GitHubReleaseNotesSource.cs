using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot;

public sealed class GitHubReleaseNotesSource(HttpClient client, ReleaseAnnouncementSettings settings) : IReleaseNotesSource
{
    public async Task<string[]> GetTitles(string? previousVersion, string version, CancellationToken cancellation)
    {
        if (!ReleaseAnnouncementSettings.IsCommit(version)
            || previousVersion != null && !ReleaseAnnouncementSettings.IsCommit(previousVersion))
            throw new ArgumentException("Release notes require full commit SHAs.");
        var commits = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { version };
        if (previousVersion != null)
        {
            using var comparison = await Get($"compare/{previousVersion}...{version}", cancellation);
            var data = comparison.RootElement;
            if (data.GetProperty("total_commits").GetInt32() > settings.MaxCommits)
                throw new InvalidOperationException("Release comparison exceeds ReleaseNotesMaxCommits; review the baseline or limit.");
            foreach (var commit in data.GetProperty("commits").EnumerateArray())
                commits.Add(commit.GetProperty("sha").GetString()!);
        }
        var titles = new Dictionary<int, string>();
        foreach (var commit in commits)
        {
            using var pulls = await Get($"commits/{commit}/pulls?per_page=100", cancellation);
            foreach (var pull in pulls.RootElement.EnumerateArray())
            {
                if (pull.GetProperty("merged_at").ValueKind == JsonValueKind.Null
                    || !string.Equals(pull.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString(),
                        settings.Repository, StringComparison.OrdinalIgnoreCase)
                    || !commits.Contains(pull.GetProperty("merge_commit_sha").GetString() ?? string.Empty))
                    continue;
                var title = pull.GetProperty("title").GetString() ?? string.Empty;
                if (title.Length > 300)
                    title = title[..(char.IsHighSurrogate(title[299]) ? 299 : 300)];
                titles[pull.GetProperty("number").GetInt32()] = title;
            }
        }
        return titles.OrderBy(x => x.Key).Select(x => x.Value).ToArray();
    }

    private async Task<JsonDocument> Get(string path, CancellationToken cancellation)
    {
        var json = await client.GetStringAsync($"repos/{settings.Repository}/{path}", cancellation);
        return JsonDocument.Parse(json);
    }
}
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http.Headers;
using Ungerfall.ChatGpt.TelegramBot.Abstractions;
using Ungerfall.ChatGpt.TelegramBot.Database;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public static class ReleaseAnnouncementServiceCollectionExtensions
{
    public static IServiceCollection AddReleaseAnnouncements(this IServiceCollection services)
    {
        services.AddSingleton(_ => ReleaseAnnouncementSettings.FromEnvironment());
        services.AddScoped<IReleaseAnnouncementRepository, ReleaseAnnouncementRepository>();
        services.AddScoped<ReleaseAnnouncementService>();
        services.AddHttpClient<IReleaseNotesSource, GitHubReleaseNotesSource>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("coauthored-telegrambot-release-notes");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            var token = Environment.GetEnvironmentVariable("GITHUB_RELEASE_NOTES_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }).RedactLoggedHeaders(new[] { "Authorization" });
        return services;
    }
}
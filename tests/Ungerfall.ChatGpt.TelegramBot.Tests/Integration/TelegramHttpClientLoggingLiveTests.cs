using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

[Trait("Category", "Integration")]
public class TelegramHttpClientLoggingLiveTests
{
    [SkippableTheory]
    [InlineData("tgclient")]
    [InlineData("telegram_bot_client")]
    public async Task ActualTelegramClient_KeepsCredentialsOutOfSuccessAndFailureLogs(string name)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("TELEGRAM_LOGGING_LIVE_TEST") == "1",
            "Telegram logging live test is not enabled");
        var token = Environment.GetEnvironmentVariable("IntegrationTests_TELEGRAM_BOT_TOKEN")
            ?? throw new InvalidOperationException("IntegrationTests_TELEGRAM_BOT_TOKEN is required");
        var logs = new HttpLogCapture();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddTelegramBotClient(name, _ => token);
        using var provider = services.BuildServiceProvider();
        var bot = provider.GetRequiredService<ITelegramBotClient>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        Assert.Equal("chatgpt_ungerfall_testing_bot", (await bot.GetMe(timeout.Token)).Username);
        var rejected = await Assert.ThrowsAsync<ApiRequestException>(() => bot.GetChat(0, timeout.Token));
        Assert.Equal(400, rejected.ErrorCode);

        Assert.False(logs.Contains(token), "Telegram credential appeared in logs or scopes");
        Assert.False(logs.Contains("api.telegram.org"), "Telegram URL appeared in logs or scopes");
        Assert.True(logs.Contains("StatusCode=200"), "Successful request diagnostics were missing");
        Assert.True(logs.Contains("StatusCode=400"), "Rejected request diagnostics were missing");
    }
}

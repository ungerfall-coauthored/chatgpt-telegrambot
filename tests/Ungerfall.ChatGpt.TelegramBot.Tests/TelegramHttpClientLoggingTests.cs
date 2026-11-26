using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot.Tests;

public class TelegramHttpClientLoggingTests
{
    private const string Token = "123456789:synthetic-test-token-never-a-real-credential";

    [Theory]
    [InlineData("tgclient", false)]
    [InlineData("tgclient", true)]
    [InlineData("telegram_bot_client", false)]
    [InlineData("telegram_bot_client", true)]
    public async Task TelegramRequests_DoNotLogCredentials_ButKeepSafeDiagnostics(string name, bool fail)
    {
        var logs = new HttpLogCapture();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddTelegramBotClient(name, _ => Token)
            .ConfigurePrimaryHttpMessageHandler(() => new TestTransport(fail));
        services.AddHttpClient("unrelated")
            .ConfigurePrimaryHttpMessageHandler(() => new TestTransport(false));
        using var provider = services.BuildServiceProvider();
        var bot = provider.GetRequiredService<ITelegramBotClient>();

        if (fail)
            await Assert.ThrowsAnyAsync<Exception>(() => bot.GetMe());
        else
            Assert.True((await bot.GetMe()).IsBot);

        Assert.False(logs.Contains(Token), "Telegram credential appeared in logs or scopes");
        Assert.False(logs.Contains("api.telegram.org"), "Telegram URL appeared in logs or scopes");
        Assert.True(logs.Contains("Telegram HTTP"), "Safe request diagnostics were missing");
        Assert.True(logs.Contains(fail ? "HttpRequestException" : "StatusCode=200"),
            "Safe status or failure diagnostics were missing");

        using var other = provider.GetRequiredService<IHttpClientFactory>().CreateClient("unrelated");
        using var response = await other.GetAsync("https://example.test/control");
        Assert.True(logs.Contains("System.Net.Http.HttpClient.unrelated"),
            "Other HTTP clients must retain their default diagnostics");
    }

    [Theory]
    [InlineData("tgclient")]
    [InlineData("telegram_bot_client")]
    public async Task RejectedTelegramRequests_DoNotLogCredentials(string name)
    {
        var logs = new HttpLogCapture();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddTelegramBotClient(name, _ => Token)
            .ConfigurePrimaryHttpMessageHandler(() => new TestTransport(false, HttpStatusCode.Unauthorized));
        using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<ApiRequestException>(() => provider.GetRequiredService<ITelegramBotClient>().GetMe());

        Assert.False(logs.Contains(Token), "Telegram credential appeared in logs or scopes");
        Assert.True(logs.Contains("StatusCode=401"), "Rejected-request status diagnostics were missing");
    }

    private sealed class TestTransport(bool fail, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fail)
                throw new HttpRequestException($"Request to {request.RequestUri} failed");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK
                    ? """{"ok":true,"result":{"id":123456789,"is_bot":true,"first_name":"Test","username":"test_bot"}}"""
                    : """{"ok":false,"error_code":401,"description":"Unauthorized"}"""),
            });
        }
    }
}

internal sealed class HttpLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();

    public bool Contains(string value) => entries.Any(entry => entry.Contains(value, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(entries, categoryName);
    public void Dispose() { }

    private sealed class CaptureLogger(ConcurrentQueue<string> entries, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Capture(state);
            return null;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(category);
            entries.Enqueue(formatter(state, exception));
            Capture(state);
            if (exception != null) entries.Enqueue(exception.ToString());
        }

        private void Capture<TState>(TState state)
        {
            entries.Enqueue(state?.ToString() ?? "");
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                foreach (var field in fields)
                    entries.Enqueue($"{field.Key}={field.Value}");
        }
    }
}

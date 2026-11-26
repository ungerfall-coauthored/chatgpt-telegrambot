using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using Telegram.Bot;

namespace Ungerfall.ChatGpt.TelegramBot.Configuration;

public static class TelegramHttpClientServiceCollectionExtensions
{
    public static IHttpClientBuilder AddTelegramBotClient(
        this IServiceCollection services, string name, Func<IServiceProvider, string> token)
    {
        return services.AddHttpClient(name)
            // Telegram credentials are in the URL path, including default logger scopes.
            .RemoveAllLoggers()
            .AddLogger(provider => new TelegramHttpLogger(provider.GetRequiredService<ILogger<TelegramHttpLogger>>()))
            .AddTypedClient<ITelegramBotClient>((http, provider) => new TelegramBotClient(token(provider), http));
    }
}

internal sealed class TelegramHttpLogger(ILogger<TelegramHttpLogger> logger) : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request)
    {
        logger.LogInformation("Telegram HTTP {Method} started", request.Method.Method);
        return null;
    }

    public void LogRequestStop(
        object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
    {
        logger.LogInformation("Telegram HTTP {Method} completed with {StatusCode} in {ElapsedMilliseconds} ms",
            request.Method.Method, (int)response.StatusCode, elapsed.TotalMilliseconds);
    }

    public void LogRequestFailed(
        object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
    {
        logger.LogWarning("Telegram HTTP {Method} failed in {ElapsedMilliseconds} ms ({ExceptionType})",
            request.Method.Method, elapsed.TotalMilliseconds, exception.GetType().Name);
    }
}
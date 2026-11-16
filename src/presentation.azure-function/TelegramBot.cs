using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Ungerfall.ChatGpt.TelegramBot.AzureFunction;

public class TelegramBot
{
    private readonly ILogger _logger;

    public TelegramBot(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<TelegramBot>();
    }

    [Function("TelegramBot")]
    public async Task<TelegramBotOutput> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        var output = new TelegramBotOutput
        {
            HttpResponseData = req.CreateResponse(HttpStatusCode.OK),
        };
        var secret = Environment.GetEnvironmentVariable("TELEGRAM_WEBHOOK_SECRET");
        if (!req.Headers.TryGetValues("X-Telegram-Bot-Api-Secret-Token", out var values)
            || !ValidateSecret(secret, values))
        {
            output.HttpResponseData = req.CreateResponse(HttpStatusCode.Unauthorized);
            return output;
        }
        output.HttpResponseData.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        try
        {
            var body = await req.ReadAsStringAsync() ?? throw new ArgumentNullException(nameof(req));
            var update = JsonSerializer.Deserialize<Update>(body, JsonBotAPI.Options);
            if (update is null)
            {
                _logger.LogWarning("Unable to deserialize Update object.");
                return output;
            }

            output.Update = update;
        }
        catch (Exception e)
        {
            _logger.LogError("Exception: {exception}", e.Message);
        }

        return output;
    }

    public static bool ValidateSecret(string? expected, System.Collections.Generic.IEnumerable<string> values)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext()) return false;
        var actual = enumerator.Current;
        if (enumerator.MoveNext() || string.IsNullOrEmpty(actual)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
    }
}

public class TelegramBotOutput
{
    public HttpResponseData HttpResponseData { get; set; } = null!;

    [ServiceBusOutput(Const.TGBOT_UPDATES, Connection = "ServiceBusConnection")]
    public Update? Update { get; set; }
}

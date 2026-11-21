using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ungerfall.ChatGpt.TelegramBot.Configuration;

namespace Ungerfall.ChatGpt.TelegramBot;
public class DecisionsClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DecisionsClient> _logger;
    private readonly AzureDecisionsSettings _settings;
    private readonly PromptCatalog _prompts;

    public DecisionsClient(HttpClient httpClient, ILogger<DecisionsClient> logger, AzureDecisionsSettings settings,
        PromptCatalog? prompts = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings;
        _prompts = prompts ?? PromptCatalog.Load();
    }

    public bool IsConfigured => _settings.IsConfigured;

    public Task<bool> ShouldReply(string input, bool autonomous, double minProbability, CancellationToken cancellation) =>
        Evaluate(input, autonomous ? ConversationDecision.Interjection : ConversationDecision.Continuation, minProbability, cancellation);

    public Task<bool> ShouldAnswerRequest(string input, double minProbability, CancellationToken cancellation) =>
        Evaluate(input, ConversationDecision.NaturalRequest, minProbability, cancellation);

    private async Task<bool> Evaluate(string input, ConversationDecision intent, double minProbability, CancellationToken cancellation)
    {
        if (!IsConfigured || input.Length > 16384
            || !double.IsFinite(minProbability) || minProbability is < 0 or > 1) return false;
        var dataInstructions = _prompts.Decisions.UntrustedDataPrefix;
        var autonomous = intent == ConversationDecision.Interjection;
        var instructions = intent switch
        {
            ConversationDecision.NaturalRequest => _prompts.Decisions.NaturalRequest,
            ConversationDecision.Interjection => _prompts.Decisions.Interjection,
            _ => _prompts.Decisions.Continuation,
        };
        var questions = new Dictionary<string, object>
        {
            ["should_reply"] = new
            {
                type = "noul",
                instructions = dataInstructions + instructions
            }
        };
        if (autonomous)
        {
            questions["casual_context"] = new
            {
                type = "noul",
                instructions = dataInstructions + _prompts.Decisions.CasualContext
            };
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = _settings.CreateRequest();
            request.Content = JsonContent.Create(new { model = _settings.Deployment, state = input, questions });
            // Foundry requires Content-Length rather than JsonContent's default chunked transfer.
            await request.Content.LoadIntoBufferAsync(128 * 1024, timeout.Token);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Decisions returned HTTP {StatusCode}; remaining silent", (int)response.StatusCode);
                return false;
            }
            await response.Content.LoadIntoBufferAsync(32768, timeout.Token);
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!body.RootElement.TryGetProperty("answers", out var answers)
                || answers.ValueKind != JsonValueKind.Object || answers.EnumerateObject().Count() != questions.Count)
                return false;
            foreach (var name in questions.Keys)
            {
                if (!answers.TryGetProperty(name, out var answer) || answer.ValueKind != JsonValueKind.Object)
                    return false;
                if (answer.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "noul"))
                    return false;
                if (!(answer.TryGetProperty("noul", out var probability) && probability.ValueKind == JsonValueKind.Number
                    && probability.TryGetDouble(out var value)
                    && double.IsFinite(value) && value >= minProbability && value <= 1))
                    return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            _logger.LogWarning("Decisions timed out; remaining silent");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning("Decisions could not be evaluated ({ErrorType}); remaining silent", ex.GetType().Name);
            return false;
        }
    }
}

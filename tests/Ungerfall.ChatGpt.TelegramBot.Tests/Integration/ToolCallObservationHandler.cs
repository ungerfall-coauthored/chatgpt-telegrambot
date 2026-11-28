using System.Text.Json;

namespace Ungerfall.ChatGpt.TelegramBot.Tests.Integration;

internal sealed record ObservedToolCall(string Name, JsonElement Arguments);

internal sealed class ToolCallObservationHandler : DelegatingHandler
{
    public List<ObservedToolCall> Calls { get; } = new();
    public List<string[]> OfferedTools { get; } = new();
    public int ToolResultRequests { get; private set; }

    public ToolCallObservationHandler() : base(new HttpClientHandler()) { }

    public void Reset()
    {
        Calls.Clear();
        OfferedTools.Clear();
        ToolResultRequests = 0;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation)))
        {
            OfferedTools.Add(body.RootElement.TryGetProperty("tools", out var tools)
                ? tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).ToArray()
                : Array.Empty<string>());
            if (body.RootElement.GetProperty("messages").EnumerateArray()
                .Any(message => message.GetProperty("role").GetString() == "tool"))
                ToolResultRequests++;
        }
        var response = await base.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) return response;
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        foreach (var choice in result.RootElement.GetProperty("choices").EnumerateArray())
        {
            if (!choice.GetProperty("message").TryGetProperty("tool_calls", out var calls)) continue;
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                using var arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
                Calls.Add(new(function.GetProperty("name").GetString()!, arguments.RootElement.Clone()));
            }
        }
        return response;
    }
}

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Ungerfall.ChatGpt.TelegramBot;

public sealed record ImageGenerationResult(bool Successful, string? Base64, string? ErrorMessage);

public interface IImageGenerationClient
{
    Task<ImageGenerationResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default);
}

public sealed class AzureImageGenerationClient(
    HttpClient httpClient, string endpoint, string apiKey, string deployment, string apiVersion) : IImageGenerationClient, IDisposable
{
    public async Task<ImageGenerationResult> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(new Uri(endpoint),
            $"openai/deployments/{Uri.EscapeDataString(deployment)}/images/generations?api-version={Uri.EscapeDataString(apiVersion)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("api-key", apiKey);
        request.Content = JsonContent.Create(new { prompt, size = "1024x1024", n = 1 });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? message;
            try
            {
                message = JsonSerializer.Deserialize<AzureImageResponse>(body)?.Error?.Message;
            }
            catch (JsonException)
            {
                message = null;
            }
            return new ImageGenerationResult(false, null,
                message ?? $"Azure image generation failed with HTTP {(int)response.StatusCode}.");
        }

        var result = JsonSerializer.Deserialize<AzureImageResponse>(body);
        var base64 = result?.Data is { Count: > 0 } ? result.Data[0].Base64 : null;
        return string.IsNullOrWhiteSpace(base64)
            ? new ImageGenerationResult(false, null, "Azure image generation returned no image data.")
            : new ImageGenerationResult(true, base64, null);
    }

    public void Dispose() => httpClient.Dispose();

    private sealed class AzureImageResponse
    {
        [JsonPropertyName("data")]
        public List<AzureImageData>? Data { get; set; }

        [JsonPropertyName("error")]
        public AzureImageError? Error { get; set; }
    }

    private sealed class AzureImageData
    {
        [JsonPropertyName("b64_json")]
        public string? Base64 { get; set; }
    }

    private sealed class AzureImageError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}

using OpenAI;
using OpenAI.Embeddings;
using SharpToken;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed record HistoryEmbeddings(float[][] Vectors, int InputTokens);

public sealed class HistoryEmbeddingClient
{
    public const int Dimensions = 512;
    private readonly EmbeddingClient _client;
    private readonly GptEncoding _encoding = GptEncoding.GetEncoding("cl100k_base");

    public HistoryEmbeddingClient(string endpoint, string deployment, string apiKey, HttpClient? httpClient = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Embeddings require an HTTPS resource endpoint without a path or credentials.");
        if (string.IsNullOrWhiteSpace(deployment) || string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Embedding deployment and API key are required.");
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(uri, "openai/v1/"),
            NetworkTimeout = TimeSpan.FromSeconds(30),
        };
        if (httpClient != null) options.Transport = new HttpClientPipelineTransport(httpClient);
        _client = new EmbeddingClient(deployment, new ApiKeyCredential(apiKey), options);
    }

    public static HistoryEmbeddingClient FromEnvironment() => new(
        Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_ENDPOINT")
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_DEPLOYMENT") ?? "",
        Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_API_KEY") ?? "");

    public int CountTokens(string text) => _encoding.Encode(text).Count;

    public async Task<HistoryEmbeddings> EmbedAsync(string[] inputs, CancellationToken cancellation)
    {
        if (inputs.Length is < 1 or > 32 || inputs.Any(text => string.IsNullOrWhiteSpace(text)
            || text.Length > 8000 || CountTokens(text) > 2000))
            throw new ArgumentException("An embedding batch must contain 1-32 nonempty inputs of at most 2000 tokens.");
        var result = (await _client.GenerateEmbeddingsAsync(inputs,
            new EmbeddingGenerationOptions { Dimensions = Dimensions }, cancellation)).Value;
        var ordered = result.OrderBy(embedding => embedding.Index).ToArray();
        if (!ordered.Select(embedding => embedding.Index).SequenceEqual(Enumerable.Range(0, inputs.Length))
            || result.Usage == null || result.Usage.InputTokenCount < 1)
            throw new InvalidOperationException("The embedding provider returned an invalid batch.");
        var vectors = ordered.Select(embedding => embedding.ToFloats().ToArray()).ToArray();
        if (vectors.Any(vector => vector.Length != Dimensions
            || vector.Any(value => !float.IsFinite(value)) || vector.All(value => value == 0)))
            throw new InvalidOperationException("The embedding provider returned an invalid batch.");
        return new HistoryEmbeddings(vectors, result.Usage.InputTokenCount);
    }
}

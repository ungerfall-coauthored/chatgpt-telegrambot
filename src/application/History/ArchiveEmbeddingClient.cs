using System;
using System.ClientModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Embeddings;
using SharpToken;

namespace Ungerfall.ChatGpt.TelegramBot.History;

public sealed class ArchiveEmbeddingClient
{
    private readonly Lazy<EmbeddingClient> _client = new(CreateClient);
    private readonly GptEncoding _encoding = GptEncoding.GetEncoding("cl100k_base");
    public const string Profile = "text-embedding-3-small:512:v1";

    private static EmbeddingClient CreateClient()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_ENDPOINT")
            ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Archive embeddings require an HTTPS resource root.");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_DEPLOYMENT")
            ?? throw new InvalidOperationException("Embedding deployment is missing.");
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDINGS_API_KEY")
            ?? throw new InvalidOperationException("Embedding credential is missing.");
        return new EmbeddingClient(deployment, new ApiKeyCredential(key), new OpenAIClientOptions
        {
            Endpoint = new Uri(uri, "openai/v1/"), NetworkTimeout = TimeSpan.FromSeconds(30),
        });
    }

    public int CountTokens(string text) => _encoding.CountTokens(text);

    public async Task<(float[][] Vectors, int Tokens)> Embed(string[] inputs, CancellationToken cancellation)
    {
        if (inputs.Length is < 1 or > 32 || inputs.Any(text => string.IsNullOrWhiteSpace(text) || CountTokens(text) > 2000))
            throw new ArgumentException("Invalid archive embedding batch.");
        var response = (await _client.Value.GenerateEmbeddingsAsync(inputs,
            new EmbeddingGenerationOptions { Dimensions = 512 }, cancellation)).Value;
        var ordered = response.OrderBy(item => item.Index).ToArray();
        if (!ordered.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, inputs.Length))
            || response.Usage == null || response.Usage.InputTokenCount < 1)
            throw new InvalidOperationException("Invalid embedding response shape.");
        var vectors = ordered.Select(item => item.ToFloats().ToArray()).ToArray();
        if (vectors.Any(vector => vector.Length != 512 || vector.Any(value => !float.IsFinite(value))
            || vector.All(value => value == 0)))
            throw new InvalidOperationException("Invalid archive embedding vectors.");
        return (vectors, response.Usage.InputTokenCount);
    }
}

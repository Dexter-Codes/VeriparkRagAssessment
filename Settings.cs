using System.ComponentModel.DataAnnotations;

namespace VeriparkRagAssessment;

public sealed class AzureOpenAiSettings
{
    [Required, Url] public string Endpoint { get; init; } = "";
    [Required] public string ApiKey { get; init; } = "";
    [Required] public string ChatDeployment { get; init; } = "";
    [Required] public string EmbeddingDeployment { get; init; } = "";
}

public sealed class QdrantSettings
{
    [Required] public string Host { get; init; } = "";
    public int Port { get; init; }
    [Required] public string CollectionName { get; init; } = "";
}

public sealed class RagSettings
{
    [Required] public string PdfPath { get; init; } = "";
    [Range(1, int.MaxValue)] public int ChunkTokens { get; init; }
    [Range(0, int.MaxValue)] public int OverlapTokens { get; init; }
    [Range(1, int.MaxValue)] public int RetrievedChunks { get; init; }
    [Range(1, int.MaxValue / 2)] public int HistoryTurns { get; init; }
}

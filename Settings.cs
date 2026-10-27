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
    [Required] public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 6334;
    [Required] public string CollectionName { get; init; } = "copilot_licensing";
}

public sealed class RagSettings
{
    public string PdfPath { get; init; } = "Documents/Microsoft-Copilot-Studio-Licensing-Guide.pdf";
    public int ChunkTokens { get; init; } = 500;
    public int OverlapTokens { get; init; } = 75;
    public int RetrievedChunks { get; init; } = 6;
    public int HistoryTurns { get; init; } = 12;
}

using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using VeriparkRagAssessment.Models;

namespace VeriparkRagAssessment.Services;

public sealed class QdrantDocumentStore(QdrantClient client, IOptions<QdrantSettings> options)
{
    private readonly string _collectionName = options.Value.CollectionName;
    public SemaphoreSlim thread { get; } = new(1, 1);

    public async Task<bool> IsIndexedAsync(string version, int expectedChunks, CancellationToken cancellationToken)
    {
        if (!await client.CollectionExistsAsync(_collectionName, cancellationToken))
            return false;

        var count = await client.CountAsync(_collectionName,
            filter: Conditions.MatchKeyword("version", version), cancellationToken: cancellationToken);
        var total = await client.CountAsync(_collectionName, cancellationToken: cancellationToken);
        return count == (ulong)expectedChunks && total == count;
    }

    public async Task ReplaceDocumentAsync(string version, string fileName,
        IReadOnlyList<DocumentChunk> chunks, IReadOnlyList<float[]> vectors, CancellationToken cancellationToken)
    {
        var dimensions = vectors[0].Length;
        if (dimensions == 0 || vectors.Any(vector => vector.Length != dimensions || vector.Any(value => !float.IsFinite(value))))
            throw new InvalidOperationException("The embedding service returned invalid vectors.");

        if (await client.CollectionExistsAsync(_collectionName, cancellationToken))
            await client.DeleteCollectionAsync(_collectionName, cancellationToken: cancellationToken);

        await client.CreateCollectionAsync(_collectionName,
            new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
            cancellationToken: cancellationToken);

        var points = chunks.Select((chunk, index) => new PointStruct
        {
            Id = chunk.Id,
            Vectors = vectors[index],
            Payload =
            {
                ["version"] = version,
                ["fileName"] = fileName,
                ["pageNumber"] = chunk.PageNumber,
                ["text"] = chunk.Text
            }
        });
        foreach (var batch in points.Chunk(32))
            await client.UpsertAsync(_collectionName, batch, wait: true, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<SourcePassage>> FindRelevantChunksAsync(
        float[] vector, int count, CancellationToken cancellationToken)
    {
        if (!await client.CollectionExistsAsync(_collectionName, cancellationToken))
            throw new InvalidOperationException("Index the PDF before asking document questions");

        var collection = await client.GetCollectionInfoAsync(_collectionName, cancellationToken);
        if (collection.Config.Params.VectorsConfig.Params.Size != (ulong)vector.Length)
            throw new InvalidOperationException("Embedding dimensions changed, re-index the Pdf");

        var matches = await client.QueryAsync(_collectionName, vector, limit: (ulong)count,
            payloadSelector: false, cancellationToken: cancellationToken);
        if (matches.Count == 0)
            return [];

        // Sequential chunk IDs let us include nearby headers, rows and next-page footnotes.
        var ids = matches.SelectMany(match => new[] { match.Id.Num - 1, match.Id.Num, match.Id.Num + 1 })
            .Where(id => id > 0).Distinct().Select(id => (PointId)id).ToArray();
        var passages = await client.RetrieveAsync(_collectionName, ids, withPayload: true,
            withVectors: false, cancellationToken: cancellationToken);

        return passages.OrderBy(passage => passage.Id.Num).Select((passage, index) => new SourcePassage(index + 1,
            passage.Payload["fileName"].StringValue,
            (int)passage.Payload["pageNumber"].IntegerValue,
            passage.Payload["text"].StringValue)).ToArray();
    }
}

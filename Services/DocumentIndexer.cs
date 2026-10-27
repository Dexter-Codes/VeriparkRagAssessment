using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using VeriparkRagAssessment.Models;

namespace VeriparkRagAssessment.Services;

public sealed class DocumentIndexer(
    IWebHostEnvironment environment,
    IOptions<RagSettings> options,
    IOptions<AzureOpenAiSettings> azureOptions,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    QdrantDocumentStore store)
{
    private readonly RagSettings _settings = options.Value;
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");

    public async Task<IndexResult> IndexPdfAsync(CancellationToken cancellationToken, bool force = false)
    {
        var path = Path.GetFullPath(_settings.PdfPath, environment.ContentRootPath);
        if (!File.Exists(path))
            throw new FileNotFoundException("pdf not found.");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        // Changing the file or chunk settings must not reuse an older index.
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var embeddingSettings = azureOptions.Value;
        var version = $"{fingerprint}:{_settings.ChunkTokens}:{_settings.OverlapTokens}:" +
            $"{embeddingSettings.Endpoint}:{embeddingSettings.EmbeddingDeployment}:v1";
        var pages = ReadPages(bytes);
        var chunks = CreateChunks(pages);
        if (chunks.Count == 0)
            throw new InvalidDataException("The PDF contains no readable text");

        var fileName = Path.GetFileName(path);
        if (!force && await store.IsIndexedAsync(version, chunks.Count, cancellationToken))
            return new IndexResult(fileName, pages.Count, chunks.Count, true);

        var vectors = new List<float[]>();
        foreach (var batch in chunks.Chunk(16))
        {
            var result = await embeddings.GenerateAsync(batch.Select(chunk => chunk.Text),
                cancellationToken: cancellationToken);
            if (result.Count != batch.Length)
                throw new InvalidOperationException("The embedding service returned an incomplete batch.");
            vectors.AddRange(result.Select(embedding => embedding.Vector.ToArray()));
        }

        await store.ReplaceDocumentAsync(version, fileName, chunks, vectors, cancellationToken);
        return new IndexResult(fileName, pages.Count, chunks.Count, false);
    }

    public static IReadOnlyList<string> ReadPages(byte[] content)
    {
        try
        {
            using var document = PdfDocument.Open(content);
            if (document.NumberOfPages > 100)
                throw new InvalidDataException("Page limit exceeded 100 pages");

            return document.GetPages()
                .Select(ReadPageText)
                .ToArray();
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("The file could not be read as Pdf", exception);
        }
    }

    private static string ReadPageText(Page page)
    {
        var words = page.GetWords().OrderByDescending(word => word.BoundingBox.Bottom).ToArray();
        if (words.Length == 0)
            return "";

        var rows = new List<List<Word>>();
        foreach (var word in words)
        {
            var row = rows.LastOrDefault();
            // Small baseline differences are common with mixed fonts and footnote markers.
            if (row is null || Math.Abs(row[0].BoundingBox.Bottom - word.BoundingBox.Bottom) > 3)
                rows.Add(row = []);
            row.Add(word);
        }

        var leftMargin = words.Min(word => word.BoundingBox.Left);
        var text = new StringBuilder();
        double? previousBottom = null;
        foreach (var row in rows)
        {
            var bottom = row[0].BoundingBox.Bottom;
            if (previousBottom.HasValue)
                text.Append(previousBottom.Value - bottom > 20 ? "\n\n" : "\n");

            var line = new StringBuilder();
            var previousRight = leftMargin;
            foreach (var word in row.OrderBy(word => word.BoundingBox.Left))
            {
                // Keep visible column gaps and indented continuation lines without guessing cell values.
                var column = (int)Math.Round((word.BoundingBox.Left - leftMargin) / 5);
                var spaces = line.Length == 0 || word.BoundingBox.Left - previousRight > 10
                    ? Math.Max(line.Length == 0 ? 0 : 2, column - line.Length)
                    : 1;
                line.Append(' ', spaces).Append(word.Text);
                previousRight = word.BoundingBox.Right;
            }
            text.Append(line);
            previousBottom = bottom;
        }
        return text.ToString();
    }

    public IReadOnlyList<DocumentChunk> CreateChunks(IReadOnlyList<string> pages)
    {
        var chunks = new List<DocumentChunk>();
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            // Keep pages separate so every citation has an unambiguous source page.
            var paragraphs = Regex.Split(pages[pageIndex].Trim(), @"\n\s*\n")
                .Where(text => !string.IsNullOrWhiteSpace(text));
            var current = "";
            foreach (var paragraph in paragraphs)
            {
                foreach (var part in SplitLongParagraph(paragraph))
                {
                    var candidate = current.Length == 0 ? part : current + "\n\n" + part;
                    if (_tokenizer.CountTokens(candidate) > _settings.ChunkTokens && current.Length > 0)
                    {
                        chunks.Add(new DocumentChunk((ulong)chunks.Count + 1, pageIndex + 1, current));
                        var overlap = GetOverlap(current);
                        current = overlap.Length > 0 ? overlap + "\n\n" + part : part;
                        if (_tokenizer.CountTokens(current) > _settings.ChunkTokens)
                            current = part;
                    }
                    else
                    {
                        current = candidate;
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(current))
                chunks.Add(new DocumentChunk((ulong)chunks.Count + 1, pageIndex + 1, current));
        }
        return chunks;
    }

    private IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        var remaining = paragraph.Trim();
        while (_tokenizer.CountTokens(remaining) > _settings.ChunkTokens)
        {
            var length = _tokenizer.GetIndexByTokenCount(remaining, _settings.ChunkTokens, out _, out _);

            // Prefer a line boundary to avoid separating a table row or sentence.
            var lineEnd = remaining.LastIndexOf('\n', length - 1, length);
            if (lineEnd > length / 2)
                length = lineEnd;
            yield return remaining[..length].Trim();
            var overlap = GetOverlap(remaining[..length]);
            remaining = overlap + "\n" + remaining[length..].TrimStart();
        }
        if (remaining.Length > 0)
            yield return remaining;
    }

    private string GetOverlap(string text)
    {
        if (_settings.OverlapTokens == 0)
            return "";
        var tokens = _tokenizer.EncodeToIds(text);

        // Use token boundaries and remove any incomplete Unicode character at the edge.
        return _tokenizer.Decode(tokens.Skip(Math.Max(0, tokens.Count - _settings.OverlapTokens)))
            .TrimStart('\uFFFD').Trim();
    }
}

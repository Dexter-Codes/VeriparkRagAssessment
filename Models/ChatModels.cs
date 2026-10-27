namespace VeriparkRagAssessment.Models;

public sealed record ChatRequest(Guid? ConversationId, string Question);
public sealed record ChatResponse(Guid ConversationId, string Answer, IReadOnlyList<SourcePassage> Sources);
public sealed record SourcePassage(int Number, string FileName, int PageNumber, string Text);
public sealed record DocumentChunk(ulong Id, int PageNumber, string Text);
public sealed record IndexResult(string FileName, int Pages, int Chunks, bool AlreadyIndexed);

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using VeriparkRagAssessment.Models;
using ChatResponse = VeriparkRagAssessment.Models.ChatResponse;

namespace VeriparkRagAssessment.Services;

public sealed class ChatService(Kernel kernel, IEmbeddingGenerator<string, Embedding<float>> embeddings,
    QdrantDocumentStore store, ConversationMemory memory, IOptions<RagSettings> options)
{
    private readonly RagSettings _settings = options.Value;

    public async Task<ChatResponse> AnswerAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
            throw new ArgumentException("Character length exceeded 2000 characters or question is empty", nameof(request.Question));

        var (conversationId, conversation) = memory.GetOrCreateConversation(request.ConversationId);
        await conversation.thread.WaitAsync(cancellationToken);
        try
        {
            var recallPosition = ParseRecallIntent(question);
            if (recallPosition.HasValue)
            {
                // Read earlier messages before storing the current recall request.
                var recalled = GetRecalledQuestion(conversation, recallPosition.Value);
                conversation.AddConversation(question, recalled, _settings.HistoryTurns);
                return new ChatResponse(conversationId, recalled, []);
            }

            var resolvedQuestion = await ResolveFollowUpQuestionAsync(question, conversation, cancellationToken);

            // Generate embedding for the user query and retrieve relevant chunks
            var result = await embeddings.GenerateAsync([resolvedQuestion], cancellationToken: cancellationToken);
            var queryVector = result.Count == 1 ? result[0].Vector.ToArray() : [];
            if (queryVector.Length == 0 || queryVector.Any(value => !float.IsFinite(value)))
            {
                throw new HttpRequestException("The embedding service returned an invalid query vector.");
            }
            await store.thread.WaitAsync(cancellationToken);
            IReadOnlyList<SourcePassage> sources;
            try
            {
                sources = await store.FindRelevantChunksAsync(queryVector, _settings.RetrievedChunks, cancellationToken);
            }
            finally 
            { 
                store.thread.Release(); 
            }

            var evidence = JsonSerializer.Serialize(new { originalQuestion = question, question = resolvedQuestion, sources });
            var answerText = sources.Count == 0 ? Prompts.InsufficientEvidence : await CompleteAsync(
                Prompts.AnswerQuestion, evidence, false, cancellationToken);

            if (answerText != Prompts.InsufficientEvidence)
            {
                try 
                { 
                    ValidateCitations(answerText, sources.Count); 
                }
                catch (HttpRequestException)
                {
                    // Retry once with the same evidence
                    answerText = await CompleteAsync(Prompts.AnswerQuestion +
                        $"\nUse only citation IDs [1] through [{sources.Count}]. A previous attempt failed citation validation.",
                        evidence, false, cancellationToken);
                    if (answerText != Prompts.InsufficientEvidence)
                        ValidateCitations(answerText, sources.Count);
                }
            }
            conversation.AddConversation(question, answerText, _settings.HistoryTurns);
            return new ChatResponse(conversationId, answerText, sources);
        }
        finally { conversation.thread.Release(); }
    }

    private static string GetRecalledQuestion(Conversation conversation, int position)
    {
        var userQuestions = conversation.GetUserQuestions();
        if (userQuestions.Count == 0)
            return "You haven't asked an earlier question in this conversation.";

        var index = position == -1 ? userQuestions.Count - 1 : position - 1;
        if (index < 0 || index >= userQuestions.Count)
            return "I couldn't find a user message at that position in this conversation.";

        return userQuestions[index];
    }

    private async Task<string> ResolveFollowUpQuestionAsync(
        string question, Conversation conversation, CancellationToken cancellationToken)
    {
        if (conversation.History.Count == 0)
            return question;

        var input = JsonSerializer.Serialize(new
        {
            question,
            firstRetainedTurn = conversation.TurnCount - conversation.History.Count / 2 + 1,
            history = conversation.History.Select(message => new { role = message.Role.Label, text = message.Content })
        });

        var resolution = await CompleteAsync(Prompts.ResolveQuestion, input, true, cancellationToken);
        using var json = JsonDocument.Parse(resolution);
        if (!json.RootElement.TryGetProperty("question", out var resolved)
            || resolved.ValueKind != JsonValueKind.String)
            throw new HttpRequestException("The model returned an invalid follow-up question.");

        var resolvedQuestion = resolved.GetString();
        if (string.IsNullOrWhiteSpace(resolvedQuestion) || resolvedQuestion.Length > 4000)
            throw new HttpRequestException("The model could not resolve the follow-up question");

        return resolvedQuestion;
    }

    private async Task<string> CompleteAsync(string instructions, string input, bool json, CancellationToken cancellationToken)
    {
        var history = new ChatHistory(instructions);
        history.AddUserMessage(input);
        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = 0,
            MaxTokens = json ? 500 : 1200,
            ResponseFormat = json ? "json_object" : null
        };
        var result = await kernel.GetRequiredService<IChatCompletionService>()
            .GetChatMessageContentAsync(history, settings, kernel, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Content))
            throw new HttpRequestException("The model returned an empty answer");
        return result.Content.Trim();
    }

    private static int? ParseRecallIntent(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return null;

        // Ignore list numbering for intent detection; keep the original text in history.
        question = Regex.Replace(question, @"^\s*\d+[.)]\s*", "");

        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (!Regex.IsMatch(question, @"^\s*(what|which|can\s+you|could\s+you|please|recall|repeat|remind|tell\s+me)\b", options)
            || !Regex.IsMatch(question, @"\b(I|my)\b", options))
            return null;

        string[] ordinalWords =
        [
            "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth",
            "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth",
            "eighteenth", "nineteenth", "twentieth"
        ];
        var positionPattern = string.Join("|", ordinalWords) + @"|initial|earliest|last|previous|latest|\d+(?:st|nd|rd|th)?";

        // Accept both "my 3rd question" and "my question number 3".
        var match = Regex.Match(question,
            $@"(?<![\w.+-])(?<position>{positionPattern})\s+(?:user\s+)?(?:question|message)\b", options);
        if (!match.Success)
            match = Regex.Match(question,
                $@"\b(?:question|message)\s+(?:number\s+|#\s*)?(?<position>{positionPattern})(?![\w-]|\.\d)", options);

        if (!match.Success)
            return null;

        var positionText = match.Groups["position"].Value.ToLowerInvariant();
        if (positionText is "last" or "previous" or "latest")
            return -1;
        if (positionText is "initial" or "earliest")
            return 1;

        var wordIndex = Array.IndexOf(ordinalWords, positionText);
        if (wordIndex >= 0)
            return wordIndex + 1;

        var digits = Regex.Match(positionText, @"^\d+").Value;
        return int.TryParse(digits, out var position) ? position : 0;
    }

    public static void ValidateCitations(string answer, int sourceCount)
    {
        var matches = Regex.Matches(answer, @"\[(\d+)\]");
        if (matches.Count == 0 || matches.Any(match => !int.TryParse(match.Groups[1].Value, out var number)
                || number < 1 || number > sourceCount))
            throw new HttpRequestException("The model returned missing or invalid source citations. Please retry.");
    }
}

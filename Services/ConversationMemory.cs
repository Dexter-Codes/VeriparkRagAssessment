using System.Collections.Concurrent;
using Microsoft.SemanticKernel.ChatCompletion;

namespace VeriparkRagAssessment.Services;

public sealed class ConversationMemory
{
    private readonly ConcurrentDictionary<Guid, Conversation> _conversations = new();

    public (Guid Id, Conversation Conversation) GetOrCreateConversation(Guid? conversationId)
    {
        if (conversationId.HasValue)
        {
            if (!_conversations.TryGetValue(conversationId.Value, out var existing))
                throw new KeyNotFoundException("Conversation not found");
            return (conversationId.Value, existing);
        }

        if (_conversations.Count >= 100)
            throw new InvalidOperationException("Reached session limit, restart it to clear memory");

        var id = Guid.NewGuid();
        var conversation = new Conversation();
        _conversations[id] = conversation;
        return (id, conversation);
    }
}

public sealed class Conversation
{
    private readonly List<string> _userQuestions = [];

    public SemaphoreSlim thread { get; } = new(1, 1);
    public ChatHistory History { get; } = [];
    public int TurnCount => _userQuestions.Count;

    public IReadOnlyList<string> GetUserQuestions() => _userQuestions;

    public void AddConversation(string question, string answer, int historyTurns)
    {
        _userQuestions.Add(question);
        History.AddUserMessage(question);
        History.AddAssistantMessage(answer);
        while (History.Count > historyTurns * 2)
            History.RemoveAt(0);
    }
}

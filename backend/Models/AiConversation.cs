namespace MoneyTracker.Models;

/// <summary>A saved conversation with the AI assistant. Title and messages are encrypted with the user's DEK.</summary>
public class AiConversation
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
    public string TitleEncrypted { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AiMessage> Messages { get; set; } = [];
}

/// <summary>One turn: what the user asked ("user") or the assistant's answer ("assistant").</summary>
public class AiMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public AiConversation Conversation { get; set; } = null!;
    public string Role { get; set; } = "user";
    public string ContentEncrypted { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

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
    public ICollection<AiProposal> Proposals { get; set; } = [];
}

/// <summary>
/// A change the assistant proposed in an answer (see AiProposals). Nothing
/// happens until the user applies it. Payload (what to change, as JSON) and
/// Result are encrypted with the user's DEK.
/// </summary>
public class AiProposal
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int MessageId { get; set; }
    public AiMessage Message { get; set; } = null!;
    /// <summary>set_category | link_transfers | rename_payee | merge_payees | mapping_rule | create_bill</summary>
    public string Kind { get; set; } = "";
    public string PayloadEncrypted { get; set; } = "";
    /// <summary>pending | applied | dismissed</summary>
    public string Status { get; set; } = "pending";
    public string? ResultEncrypted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
}

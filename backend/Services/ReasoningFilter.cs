using System.Text;
using System.Text.RegularExpressions;

namespace MoneyTracker.Services;

/// <summary>
/// Hides a model's reasoning ("thinking") from answers. Reasoning models
/// (Qwen3, DeepSeek-R1, …) often put it in the answer text inside
/// &lt;think&gt;…&lt;/think&gt; (or &lt;thinking&gt;); reasoning sent in a separate
/// field (reasoning_content) is never read at all.
/// Push() filters streamed pieces as they arrive, holding back a possible
/// half-received tag; Strip() cleans a whole answer.
/// </summary>
public sealed partial class ReasoningFilter
{
    private static readonly string[] Opens = ["<thinking>", "<think>"];
    private static readonly string[] Closes = ["</thinking>", "</think>"];
    private readonly StringBuilder pending = new();
    private bool inside;

    /// <summary>True once any reasoning has been seen.</summary>
    public bool SawReasoning { get; private set; }

    /// <summary>The visible part of the next piece of streamed text (may be empty).</summary>
    public string Push(string piece)
    {
        pending.Append(piece);
        var text = pending.ToString();
        pending.Clear();
        var visible = new StringBuilder();
        while (text.Length > 0)
        {
            if (inside)
            {
                var (at, len) = Find(text, Closes);
                if (at < 0)
                {
                    // Keep a tail that could be the start of the closing tag.
                    var keep = Math.Min(text.Length, Closes[0].Length - 1);
                    pending.Append(text[^keep..]);
                    return visible.ToString();
                }
                inside = false;
                text = text[(at + len)..];
                continue;
            }
            var (open, olen) = Find(text, Opens);
            if (open >= 0)
            {
                visible.Append(text[..open]);
                inside = true;
                SawReasoning = true;
                text = text[(open + olen)..];
                continue;
            }
            // Hold back a trailing "<", "<th", … that might become an opening tag.
            var hold = PartialTagLength(text);
            visible.Append(text[..^hold]);
            pending.Append(text[^hold..]);
            break;
        }
        return visible.ToString();
    }

    /// <summary>At the end of the stream: whatever was held back, unless it's unfinished reasoning.</summary>
    public string Flush()
    {
        var rest = inside ? "" : pending.ToString();
        pending.Clear();
        return rest;
    }

    private static (int At, int Len) Find(string text, string[] tags)
    {
        foreach (var t in tags)
        {
            var i = text.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return (i, t.Length);
        }
        return (-1, 0);
    }

    private static int PartialTagLength(string text)
    {
        var lt = text.LastIndexOf('<');
        if (lt < 0) return 0;
        var tail = text[lt..];
        return Opens.Any(o => o.StartsWith(tail, StringComparison.OrdinalIgnoreCase) && tail.Length < o.Length) ? tail.Length : 0;
    }

    [GeneratedRegex(@"<think(?:ing)?>[\s\S]*?(?:</think(?:ing)?>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Blocks();

    /// <summary>The answer without reasoning: tagged blocks removed, and anything before a lone closing tag (models that omit the opening one).</summary>
    public static string Strip(string text)
    {
        var s = Blocks().Replace(text, "");
        var close = Math.Max(s.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase), s.LastIndexOf("</thinking>", StringComparison.OrdinalIgnoreCase));
        if (close >= 0) s = s[(s.IndexOf('>', close) + 1)..];
        return s.Trim();
    }
}

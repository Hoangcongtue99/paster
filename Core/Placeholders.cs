using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Paster.Core;

/// <summary>Text to paste, plus what to do with the caret afterwards.</summary>
/// <param name="Text">Resolved text, "\n" newlines, markers removed.</param>
/// <param name="LeftMoves">Left-arrow presses to walk back from the end of the paste.</param>
/// <param name="SelectLength">Shift+Left presses after that, to select a run of text.</param>
public sealed record ResolvedPaste(string Text, int LeftMoves, int SelectLength)
{
    public bool MovesCaret => LeftMoves > 0 || SelectLength > 0;
}

public static class Placeholders
{
    public const string CursorToken = "{{cursor}}";
    public const string SelectToken = "{{select:TEXT}}";

    /// <summary>
    /// Matches {{cursor}} and {{select:...}}. The payload is lazy and excludes
    /// "}" so a snippet can still contain literal braces after the marker.
    /// </summary>
    public static readonly Regex Pattern = new(
        @"\{\{cursor\}\}|\{\{select:(?<sel>[^}]*)\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Strips every marker from the body. The FIRST marker decides where the
    /// caret ends up; later ones are still unwrapped (a {{select:X}} leaves X
    /// behind, a {{cursor}} vanishes) so nothing leaks into the pasted text.
    /// </summary>
    public static ResolvedPaste Resolve(string body)
    {
        body = Snippet.Normalize(body);

        var matches = Pattern.Matches(body);
        if (matches.Count == 0) return new ResolvedPaste(body, 0, 0);

        var sb = new StringBuilder(body.Length);
        int cursor = 0;          // read position in body
        int anchorStart = -1;    // where the first marker landed in the output
        int anchorLength = 0;    // length of the text it wants selected

        foreach (Match m in matches)
        {
            sb.Append(body, cursor, m.Index - cursor);
            cursor = m.Index + m.Length;

            var selection = m.Groups["sel"].Success ? m.Groups["sel"].Value : "";

            if (anchorStart < 0)
            {
                anchorStart = sb.Length;
                anchorLength = selection.Length;
            }

            sb.Append(selection);
        }

        sb.Append(body, cursor, body.Length - cursor);

        var text = sb.ToString();
        var tail = text.Length - (anchorStart + anchorLength);

        return new ResolvedPaste(text, Math.Max(0, tail), anchorLength);
    }
}

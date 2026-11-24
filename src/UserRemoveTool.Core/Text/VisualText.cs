using System.Text;

namespace UserRemoveTool.Core.Text;

/// <summary>
/// Windows consoles (conhost and Windows Terminal) draw text strictly left to right, one cell per character,
/// without the Unicode bidirectional algorithm. Hebrew in logical order therefore appears reversed.
/// This converts a logical line to visual order: Hebrew runs are reversed, Latin text, digits, paths and
/// attribute names keep their reading order, and runs are laid out right to left.
/// </summary>
public static class VisualText
{
    private enum Dir { Ltr, Rtl, Neutral }

    public static bool ContainsRtl(string text) => text.Any(IsRtl);

    private static bool IsRtl(char c) => c is >= '֐' and <= '׿' or >= 'יִ' and <= 'ﭏ';

    private static Dir Classify(char c) => IsRtl(c) ? Dir.Rtl : char.IsLetterOrDigit(c) ? Dir.Ltr : Dir.Neutral;

    private static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<', _ => c
    };

    /// <summary>Converts one line. Lines without Hebrew are returned unchanged.</summary>
    public static string ToVisual(string logical)
    {
        if (!ContainsRtl(logical)) return logical;

        var dir = logical.Select(Classify).ToArray();
        // A word with Latin letters or digits and no Hebrew (path, OU=..., e-mail, attribute name) is one left-to-right
        // unit including its punctuation, so \\server\share or (danac) keep their order and brackets.
        for (var i = 0; i < logical.Length;)
        {
            if (logical[i] == ' ') { i++; continue; }
            var end = i;
            while (end < logical.Length && logical[end] != ' ') end++;
            if (dir[i..end].Contains(Dir.Ltr) && !dir[i..end].Contains(Dir.Rtl))
                for (var k = i; k < end; k++) dir[k] = Dir.Ltr;
            i = end;
        }
        // Neutrals (spaces, punctuation) take the direction of the text around them, otherwise the base direction (RTL).
        for (var i = 0; i < dir.Length;)
        {
            if (dir[i] != Dir.Neutral) { i++; continue; }
            var end = i;
            while (end < dir.Length && dir[end] == Dir.Neutral) end++;
            var before = i > 0 ? dir[i - 1] : Dir.Rtl;
            var after = end < dir.Length ? dir[end] : Dir.Rtl;
            var resolved = before == after ? before : Dir.Rtl;
            for (var k = i; k < end; k++) dir[k] = resolved;
            i = end;
        }

        var runs = new List<(Dir Dir, string Text)>();
        for (var i = 0; i < logical.Length;)
        {
            var end = i;
            while (end < logical.Length && dir[end] == dir[i]) end++;
            runs.Add((dir[i], logical[i..end]));
            i = end;
        }

        var sb = new StringBuilder(logical.Length);
        for (var r = runs.Count - 1; r >= 0; r--)
        {
            var (d, text) = runs[r];
            if (d == Dir.Ltr) { sb.Append(text); continue; }
            for (var i = text.Length - 1; i >= 0; i--) sb.Append(Mirror(text[i]));
        }
        return sb.ToString();
    }

    /// <summary>Breaks a logical line at spaces so that each piece fits the width. Wrapping must happen before reordering.</summary>
    public static IEnumerable<string> Wrap(string logical, int width)
    {
        if (width < 10 || logical.Length <= width) { yield return logical; yield break; }
        var indent = logical.Length - logical.TrimStart().Length;
        var line = new StringBuilder();
        foreach (var word in logical.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && indent + line.Length + 1 + word.Length > width)
            {
                yield return new string(' ', indent) + line;
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return new string(' ', indent) + line;
    }
}

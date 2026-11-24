using System.IO;
using System.Text;
using UserRemoveTool.Core.Text;

namespace UserRemoveTool;

/// <summary>
/// Console output of Hebrew: wrapped in logical order, converted to visual order and right-aligned.
/// Only when writing to a real terminal; redirected output (files, pipes) stays in logical order.
/// </summary>
internal static class ConsoleOut
{
    public static bool VisualHebrew { get; set; } = true;

    public static void Setup()
    {
        // Without this Hebrew is garbled (?) or written in the legacy code page.
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
    }

    public static void Line(string text = "", ConsoleColor? color = null)
    {
        if (color is { } c) Console.ForegroundColor = c;
        try
        {
            foreach (var logical in text.Replace("\r", "").Split('\n'))
            {
                if (!VisualHebrew || Console.IsOutputRedirected || !VisualText.ContainsRtl(logical))
                {
                    Console.WriteLine(logical);
                    continue;
                }
                var width = Width();
                foreach (var piece in VisualText.Wrap(logical, width))
                {
                    var visual = VisualText.ToVisual(piece);
                    Console.WriteLine(new string(' ', Math.Max(0, width - visual.Length)) + visual);
                }
            }
        }
        finally { if (color is not null) Console.ResetColor(); }
    }

    private static int Width()
    {
        try { return Math.Max(20, Console.WindowWidth - 1); }
        catch (IOException) { return 79; }
    }
}

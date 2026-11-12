using System;
using System.Linq;
using System.Text;

namespace Smithingway.Util;

public static class TextSanitizer
{
    public const int MaxNameLength = 48;
    public const int MaxFlavorLength = 300;
    public const int MaxFlavorLines = 6;

    public static string CleanName(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var filtered = Filter(input, allowNewlines: false);
        var collapsed = string.Join(' ', filtered.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Truncate(collapsed, MaxNameLength);
    }

    public static string CleanFlavor(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var filtered = Filter(input.Replace("\r\n", "\n").Replace('\r', '\n'), allowNewlines: true);
        var lines = filtered.Split('\n').Select(l => l.TrimEnd()).Take(MaxFlavorLines);
        return Truncate(string.Join('\n', lines).Trim(), MaxFlavorLength);
    }

    public static bool HasUnsupportedCharacters(string? input, bool allowNewlines)
        => !string.IsNullOrEmpty(input) && Filter(input.Replace("\r", ""), allowNewlines).Length != input.Replace("\r", "").Length;

    private static string Filter(string input, bool allowNewlines)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c == '\n') { if (allowNewlines) sb.Append(c); continue; }
            if (c == '\t') { sb.Append(' '); continue; }
            if (char.IsControl(c) || char.IsSurrogate(c)) continue;
            if (c is >= '\uE000' and <= '\uF8FF') continue;
            if (c is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}

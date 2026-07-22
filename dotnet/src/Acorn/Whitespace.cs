using System.Text.RegularExpressions;

namespace Acorn;

public static class Whitespace
{
    // Matches a whole line break (where CRLF is considered a single line break).
    // JS regex (see original whitespace.js) built from code points below
    public static readonly Regex LineBreak = BuildLineBreak();
    public static readonly Regex LineBreakG = LineBreak;

    // JS regex (see original whitespace.js) built from code points below
    public static readonly Regex NonASCIIwhitespace = BuildNonAsciiWhitespace();

    // /(?:\s|\/\/.*|\/\*[^]*?\*\/)*/g  -- [^] in JS means "any char incl newlines".
    public static readonly Regex SkipWhiteSpace =
        new(@"(?:\s|//.*|/\*[\s\S]*?\*/)*", RegexOptions.Compiled);

    private static Regex BuildLineBreak()
    {
        string pattern = "\r\n?|\n|" + (char)0x2028 + "|" + (char)0x2029;
        return new Regex(pattern, RegexOptions.Compiled);
    }

    private static Regex BuildNonAsciiWhitespace()
    {
        string pattern = "[" + (char)0x1680 +
            (char)0x2000 + "-" + (char)0x200a +
            (char)0x202f + (char)0x205f + (char)0x3000 + (char)0xfeff + "]";
        return new Regex(pattern, RegexOptions.Compiled);
    }

    public static bool IsNewLine(int code) =>
        code == 10 || code == 13 || code == 0x2028 || code == 0x2029;

    public static int NextLineBreak(string code, int from) => NextLineBreak(code, from, code.Length);

    public static int NextLineBreak(string code, int from, int end)
    {
        for (int i = from; i < end; i++)
        {
            int next = code[i];
            if (IsNewLine(next))
                return i < end - 1 && next == 13 && code[i + 1] == 10 ? i + 2 : i + 1;
        }
        return -1;
    }
}

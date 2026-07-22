using System.Text;
using System.Text.RegularExpressions;

namespace Acorn;

public static class Util
{
    private static readonly Dictionary<string, Regex> RegexpCache = new();

    /// <summary>
    /// Builds an anchored alternation regex from a space-separated word list,
    /// equivalent to acorn's <c>wordsRegexp</c>.
    /// </summary>
    public static Regex WordsRegexp(string words)
    {
        if (RegexpCache.TryGetValue(words, out var cached)) return cached;
        var re = new Regex("^(?:" + words.Replace(" ", "|") + ")$", RegexOptions.Compiled);
        RegexpCache[words] = re;
        return re;
    }

    /// <summary>UTF-16 encode a code point to a string.</summary>
    public static string CodePointToString(int code)
    {
        if (code <= 0xFFFF) return ((char)code).ToString();
        code -= 0x10000;
        return new string(new[] { (char)((code >> 10) + 0xD800), (char)((code & 1023) + 0xDC00) });
    }

    // /[\uD800-\uDFFF]/u
    public static readonly Regex LoneSurrogate =
        new("[" + (char)0xD800 + "-" + (char)0xDFFF + "]", RegexOptions.Compiled);
}

using System.Text.RegularExpressions;

namespace Acorn;

public static class Identifier
{
    // Reserved word lists for various dialects of the language.
    public static readonly Dictionary<string, string> ReservedWords = new()
    {
        ["3"] = "abstract boolean byte char class double enum export extends final float goto implements import int interface long native package private protected public short static super synchronized throws transient volatile",
        ["5"] = "class enum extends super const export import",
        ["6"] = "enum",
        ["strict"] = "implements interface let package private protected public static yield",
        ["strictBind"] = "eval arguments"
    };

    private const string Ecma5AndLessKeywords =
        "break case catch continue debugger default do else finally for function if return switch throw try var while with null true false instanceof typeof void delete new in this";

    public static readonly Dictionary<string, string> Keywords = new()
    {
        ["5"] = Ecma5AndLessKeywords,
        ["5module"] = Ecma5AndLessKeywords + " export import",
        ["6"] = Ecma5AndLessKeywords + " const class extends export import super"
    };

    public static readonly Regex KeywordRelationalOperator = new("^in(stanceof)?$", RegexOptions.Compiled);

    private static readonly Regex NonASCIIidentifierStart =
        new("[" + GeneratedIdentifierData.NonASCIIidentifierStartChars + "]", RegexOptions.Compiled);

    private static readonly Regex NonASCIIidentifier =
        new("[" + GeneratedIdentifierData.NonASCIIidentifierStartChars +
            GeneratedIdentifierData.NonASCIIidentifierChars + "]", RegexOptions.Compiled);

    // Linear-complexity lookup for astral identifier characters (assumed rare).
    private static bool IsInAstralSet(int code, int[] set)
    {
        int pos = 0x10000;
        for (int i = 0; i < set.Length; i += 2)
        {
            pos += set[i];
            if (pos > code) return false;
            pos += set[i + 1];
            if (pos >= code) return true;
        }
        return false;
    }

    /// <summary>Test whether a given character code starts an identifier.</summary>
    public static bool IsIdentifierStart(int code, bool astral = true)
    {
        if (code < 65) return code == 36;
        if (code < 91) return true;
        if (code < 97) return code == 95;
        if (code < 123) return true;
        if (code <= 0xffff) return code >= 0xaa && NonASCIIidentifierStart.IsMatch(((char)code).ToString());
        if (!astral) return false;
        return IsInAstralSet(code, GeneratedIdentifierData.AstralIdentifierStartCodes);
    }

    /// <summary>Test whether a given character is part of an identifier.</summary>
    public static bool IsIdentifierChar(int code, bool astral = true)
    {
        if (code < 48) return code == 36;
        if (code < 58) return true;
        if (code < 65) return false;
        if (code < 91) return true;
        if (code < 97) return code == 95;
        if (code < 123) return true;
        if (code <= 0xffff) return code >= 0xaa && NonASCIIidentifier.IsMatch(((char)code).ToString());
        if (!astral) return false;
        return IsInAstralSet(code, GeneratedIdentifierData.AstralIdentifierStartCodes) ||
               IsInAstralSet(code, GeneratedIdentifierData.AstralIdentifierCodes);
    }
}

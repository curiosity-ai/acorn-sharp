using Acorn;
using tt = Acorn.TokenTypes;

namespace Acorn.Loose;

/// <summary>
/// Helper predicates that acorn-loose calls on its underlying tokenizer
/// (<c>this.toks.isLet()</c>, <c>this.toks.isAsyncFunction()</c>, ...). In upstream
/// acorn these are instance methods of the full <c>Parser</c> (defined in
/// statement.js / expression.js). The C# foundation ported only the tokenizer, so
/// they are provided here as extension methods on <see cref="Parser"/>; every
/// member they touch is public parser state. Ported verbatim from
/// acorn/src/statement.js and acorn/src/expression.js.
/// </summary>
public static class ToksHelpers
{
    private static string Slice(string s, int from, int to)
    {
        if (from < 0) from = 0;
        if (to > s.Length) to = s.Length;
        if (from >= to) return "";
        return s.Substring(from, to - from);
    }

    public static bool IsLet(this Parser p, bool context = false)
    {
        if (p.Options.EcmaVersion < 6 || !p.IsContextual("let")) return false;
        var skip = Whitespace.SkipWhiteSpace.Match(p.Input, p.Pos);
        int next = p.Pos + skip.Length;
        int nextCh = p.FullCharCodeAt(next);
        // For ambiguous cases, determine if a LexicalDeclaration (or only a
        // Statement) is allowed here. `let [` is an explicit negative lookahead
        // for ExpressionStatement, so special-case it first.
        if (nextCh == 91 || nextCh == 92) return true; // '[', '\'
        if (context) return false;

        if (nextCh == 123) return true; // '{'
        if (Identifier.IsIdentifierStart(nextCh))
        {
            int start = next;
            do { next += nextCh <= 0xffff ? 1 : 2; }
            while (Identifier.IsIdentifierChar(nextCh = p.FullCharCodeAt(next)));
            if (nextCh == 92) return true;
            string ident = p.Input.Substring(start, next - start);
            if (!Identifier.KeywordRelationalOperator.IsMatch(ident)) return true;
        }
        return false;
    }

    // check 'async [no LineTerminator here] function'
    public static bool IsAsyncFunction(this Parser p)
    {
        if (p.Options.EcmaVersion < 8 || !p.IsContextual("async"))
            return false;

        var skip = Whitespace.SkipWhiteSpace.Match(p.Input, p.Pos);
        int next = p.Pos + skip.Length;
        int after;
        return !Whitespace.LineBreak.IsMatch(p.Input.Substring(p.Pos, next - p.Pos)) &&
            Slice(p.Input, next, next + 8) == "function" &&
            (next + 8 == p.Input.Length ||
             !(Identifier.IsIdentifierChar(after = p.FullCharCodeAt(next + 8)) || after == 92 /* '\' */));
    }

    public static bool IsUsingKeyword(this Parser p, bool isAwaitUsing, bool isFor)
    {
        if (p.Options.EcmaVersion < 17 || !p.IsContextual(isAwaitUsing ? "await" : "using"))
            return false;

        var skip = Whitespace.SkipWhiteSpace.Match(p.Input, p.Pos);
        int next = p.Pos + skip.Length;

        if (Whitespace.LineBreak.IsMatch(p.Input.Substring(p.Pos, next - p.Pos))) return false;

        if (isAwaitUsing)
        {
            int usingEndPos = next + 5 /* using */;
            int after;
            if (Slice(p.Input, next, usingEndPos) != "using" ||
                usingEndPos == p.Input.Length ||
                Identifier.IsIdentifierChar(after = p.FullCharCodeAt(usingEndPos)) ||
                after == 92 /* '\' */)
                return false;

            var skipAfterUsing = Whitespace.SkipWhiteSpace.Match(p.Input, usingEndPos);
            next = usingEndPos + skipAfterUsing.Length;
            if (Whitespace.LineBreak.IsMatch(p.Input.Substring(usingEndPos, next - usingEndPos))) return false;
        }

        int ch = p.FullCharCodeAt(next);
        if (!Identifier.IsIdentifierStart(ch) && ch != 92 /* '\' */) return false;
        int idStart = next;
        do { next += ch <= 0xffff ? 1 : 2; }
        while (Identifier.IsIdentifierChar(ch = p.FullCharCodeAt(next)));
        if (ch == 92) return true;
        string id = p.Input.Substring(idStart, next - idStart);
        if (Identifier.KeywordRelationalOperator.IsMatch(id)) return false;
        if (isFor && !isAwaitUsing && id == "of")
        {
            // Look ahead for using declaration with initializer, i.e., `for (using of = ...)`
            var skipAfterOf = Whitespace.SkipWhiteSpace.Match(p.Input, next);
            next = next + skipAfterOf.Length;
            int c0 = next < p.Input.Length ? p.Input[next] : -1;
            int c1;
            if (c0 != 61 /* '=' */ ||
                (c1 = (next + 1 < p.Input.Length ? p.Input[next + 1] : -1)) == 61 /* '=' */ || c1 == 62 /* '>' */)
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsAwaitUsing(this Parser p, bool isFor) => p.IsUsingKeyword(true, isFor);

    public static bool IsUsing(this Parser p, bool isFor) => p.IsUsingKeyword(false, isFor);

    public static bool IsClassElementNameStart(this Parser p) =>
        p.Type == tt.Name ||
        p.Type == tt.PrivateId ||
        p.Type == tt.Num ||
        p.Type == tt.String ||
        p.Type == tt.BracketL ||
        p.Type.Keyword != null;

    public static bool IsAsyncProp(this Parser p, Node prop)
    {
        Node? key = prop["key"] as Node;
        return !(prop["computed"] as bool? ?? false) && key?.Type == "Identifier" && (key["name"] as string) == "async" &&
            (p.Type == tt.Name || p.Type == tt.Num || p.Type == tt.String || p.Type == tt.BracketL || p.Type.Keyword != null || (p.Options.EcmaVersion >= 9 && p.Type == tt.Star)) &&
            !Whitespace.LineBreak.IsMatch(p.Input.Substring(p.LastTokEnd, p.Start - p.LastTokEnd));
    }

    // Set `ExpressionStatement#directive` property for directive prologues.
    public static void AdaptDirectivePrologue(this Parser p, List<object?> statements)
    {
        for (int i = 0; i < statements.Count && p.IsDirectiveCandidate((Node)statements[i]!); ++i)
        {
            var stmt = (Node)statements[i]!;
            var expr = (Node)stmt["expression"]!;
            string raw = (string)expr["raw"]!;
            stmt["directive"] = raw.Substring(1, raw.Length - 2);
        }
    }

    private static bool IsDirectiveCandidate(this Parser p, Node statement)
    {
        Node? expr = statement["expression"] as Node;
        return
            p.Options.EcmaVersion >= 5 &&
            statement.Type == "ExpressionStatement" &&
            expr?.Type == "Literal" &&
            expr["value"] is string &&
            // Reject parenthesized strings.
            (p.Input[statement.Start] == '"' || p.Input[statement.Start] == '\'');
    }
}

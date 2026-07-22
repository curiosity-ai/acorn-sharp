using Acorn;
using tt = Acorn.TokenTypes;

namespace Acorn.Loose;

/// <summary>
/// A token-like object used by the loose parser. Mirrors the plain-object tokens
/// acorn-loose builds (`{type, value, start, end, loc}`).
/// </summary>
public sealed class LooseToken
{
    public TokenType Type = tt.Eof;
    public object? Value;
    public int Start;
    public int End;
    public SourceLocation? Loc;
}

/// <summary>
/// The result of <c>storeCurrentPos</c>: either a bare offset (locations off) or
/// an offset plus a <see cref="Position"/> (locations on).
/// </summary>
public readonly struct StoredPos
{
    public readonly int Start;
    public readonly Position? Loc;
    public StoredPos(int start, Position? loc) { Start = start; Loc = loc; }
}

// Ported from acorn-loose/src/state.js
public partial class LooseParser
{
    public Parser toks;
    public Options options;
    public string input;
    public LooseToken tok;
    public LooseToken last;
    public List<LooseToken> ahead;    // Tokens ahead
    public List<int> context;         // Indentation context
    public int curIndent;
    public int curLineStart;
    public int nextLineStart;
    public bool inAsync;
    public bool inGenerator;
    public bool inFunction;

    public LooseParser(string input, Options? options = null)
    {
        this.toks = Parser.Tokenizer(input, options);
        this.options = this.toks.Options;
        this.input = this.toks.Input;
        this.tok = this.last = new LooseToken { Type = tt.Eof, Start = 0, End = 0 };
        // JS assigns noop validateRegExpFlags/validateRegExpPattern here; irrelevant in C#.
        if (this.options.Locations)
        {
            Position? here = this.toks.CurPosition();
            this.tok.Loc = new SourceLocation(this.toks, here, here);
        }
        this.ahead = new List<LooseToken>();
        this.context = new List<int>();
        this.curIndent = 0;
        this.curLineStart = 0;
        this.nextLineStart = this.LineEnd(this.curLineStart) + 1;
        this.inAsync = false;
        this.inGenerator = false;
        this.inFunction = false;
    }

    public Node StartNode() =>
        new Node(this.toks, this.tok.Start, this.options.Locations ? this.tok.Loc!.Start : null);

    public StoredPos StoreCurrentPos() =>
        new StoredPos(this.tok.Start, this.options.Locations ? this.tok.Loc!.Start : null);

    public Node StartNodeAt(StoredPos pos) => new Node(this.toks, pos.Start, pos.Loc);

    public Node FinishNode(Node node, string type)
    {
        node.Type = type;
        node.End = this.last.End;
        if (this.options.Locations)
            node.Loc!.End = this.last.Loc!.End;
        if (this.options.Ranges)
            node.Range![1] = this.last.End;
        return node;
    }

    public Node DummyNode(string type)
    {
        Node dummy = this.StartNode();
        dummy.Type = type;
        dummy.End = dummy.Start;
        if (this.options.Locations)
            dummy.Loc!.End = dummy.Loc!.Start;
        if (this.options.Ranges)
            dummy.Range![1] = dummy.Start;
        this.last = new LooseToken { Type = tt.Name, Start = dummy.Start, End = dummy.Start, Loc = dummy.Loc };
        return dummy;
    }

    public Node DummyIdent()
    {
        Node dummy = this.DummyNode("Identifier");
        dummy["name"] = ParseUtil.DummyValue;
        return dummy;
    }

    public Node DummyString()
    {
        Node dummy = this.DummyNode("Literal");
        dummy["value"] = ParseUtil.DummyValue;
        dummy["raw"] = ParseUtil.DummyValue;
        return dummy;
    }

    public bool Eat(TokenType type)
    {
        if (this.tok.Type == type)
        {
            this.Next();
            return true;
        }
        return false;
    }

    public bool IsContextual(string name) =>
        this.tok.Type == tt.Name && (this.tok.Value as string) == name;

    public bool EatContextual(string name) =>
        (this.tok.Value as string) == name && this.Eat(tt.Name);

    public bool CanInsertSemicolon() =>
        this.tok.Type == tt.Eof || this.tok.Type == tt.BraceR ||
        Whitespace.LineBreak.IsMatch(Slice(this.input, this.last.End, this.tok.Start));

    public bool Semicolon() => this.Eat(tt.Semi);

    public bool Expect(TokenType type)
    {
        if (this.Eat(type)) return true;
        for (int i = 1; i <= 2; i++)
        {
            if (this.LookAhead(i).Type == type)
            {
                for (int j = 0; j < i; j++) this.Next();
                return true;
            }
        }
        return false;
    }

    public void PushCx() => this.context.Add(this.curIndent);

    public void PopCx()
    {
        this.curIndent = this.context[this.context.Count - 1];
        this.context.RemoveAt(this.context.Count - 1);
    }

    public int LineEnd(int pos)
    {
        while (pos < this.input.Length && !Whitespace.IsNewLine(this.input[pos])) ++pos;
        return pos;
    }

    public int IndentationAfter(int pos)
    {
        for (int count = 0; ; ++pos)
        {
            int ch = pos < this.input.Length ? this.input[pos] : -1;
            if (ch == 32) ++count;
            else if (ch == 9) count += this.options.TabSize;
            else return count;
        }
    }

    public bool Closes(TokenType closeTok, int indent, int line, bool blockHeuristic = false)
    {
        if (this.tok.Type == closeTok || this.tok.Type == tt.Eof) return true;
        return line != this.curLineStart && this.curIndent < indent && this.TokenStartsLine() &&
            (!blockHeuristic || this.nextLineStart >= this.input.Length ||
             this.IndentationAfter(this.nextLineStart) < indent);
    }

    public bool TokenStartsLine()
    {
        for (int p = this.tok.Start - 1; p >= this.curLineStart; --p)
        {
            int ch = this.input[p];
            if (ch != 9 && ch != 32) return false;
        }
        return true;
    }

    public Node Parse()
    {
        this.Next();
        try
        {
            return this.ParseTopLevel();
        }
        catch (Exception e)
        {
            if (IsStackError(e))
            {
                this.toks.Raise(this.toks.Start, "Not enough stack space to parse input");
                return null!; // unreachable: Raise throws.
            }
            throw;
        }
    }

    private static bool IsStackError(Exception e) =>
        System.Text.RegularExpressions.Regex.IsMatch(e.Message, @"\bstack\b.*\b(exceeded|overflow)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(e.Message, @"\btoo much recursion\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static Node Parse(string input, Options? options) => new LooseParser(input, options).Parse();

    // --- helpers used by the port for JS truthiness on the isStatement parameter ---

    // JS `x === true`.
    internal static bool IsExactlyTrue(object? v) => v is bool b && b;

    // JS truthiness for the isStatement/nullableID sentinel.
    internal static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length != 0,
        _ => true
    };
}

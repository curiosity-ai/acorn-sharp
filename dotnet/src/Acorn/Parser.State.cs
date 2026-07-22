using System.Text.RegularExpressions;
using tt = Acorn.TokenTypes;
using static Acorn.ScopeFlags;

namespace Acorn;

public partial class Parser
{
    public const string Version = "8.17.0";

    public Options Options;
    public string? SourceFile;
    public Regex Keywords;
    public Regex ReservedWords;
    public Regex ReservedWordsStrict;
    public Regex ReservedWordsStrictBind;
    public string Input;

    // Whether the last-read word contained an escape sequence.
    public bool ContainsEsc;

    // Tokenizer position.
    public int Pos;
    public int LineStart;
    public int CurLine;

    // Current token.
    public TokenType Type;
    public object? Value;
    public int Start;
    public int End;
    public Position? StartLoc;
    public Position? EndLoc;

    // Previous token.
    public Position? LastTokEndLoc;
    public Position? LastTokStartLoc;
    public int LastTokStart;
    public int LastTokEnd;

    // Context stack (for regexp/division disambiguation).
    public List<TokContext> Context;
    public bool ExprAllowed;

    public bool InModule;
    public bool Strict;

    public int PotentialArrowAt;
    public bool PotentialArrowInForAwait;

    public int YieldPos;
    public int AwaitPos;
    public int AwaitIdentPos;

    public List<LabelInfo> Labels;
    public Dictionary<string, Node> UndefinedExports;

    public List<Scope> ScopeStack;

    public RegExpValidationState? RegexpState;

    public List<PrivateNameStatus> PrivateNameStack;

    // Used by the tokenizer while reading template elements.
    public bool InTemplateElement;

    public Parser(Options? options, string input, int? startPos = null)
    {
        TokContexts.EnsureInitialized();
        Options = options = Options.GetOptions(options);
        SourceFile = options.SourceFile;
        Keywords = Util.WordsRegexp(Identifier.Keywords[
            options.EcmaVersion >= 6 ? "6" : options.SourceType == "module" ? "5module" : "5"]);
        string reserved = "";
        if (options.AllowReserved != AllowReservedOption.True)
        {
            reserved = Identifier.ReservedWords[
                options.EcmaVersion >= 6 ? "6" : options.EcmaVersion == 5 ? "5" : "3"];
            if (options.SourceType == "module") reserved += " await";
        }
        ReservedWords = Util.WordsRegexp(reserved);
        string reservedStrict = (reserved.Length > 0 ? reserved + " " : "") + Identifier.ReservedWords["strict"];
        ReservedWordsStrict = Util.WordsRegexp(reservedStrict);
        ReservedWordsStrictBind = Util.WordsRegexp(reservedStrict + " " + Identifier.ReservedWords["strictBind"]);
        Input = input ?? "";

        ContainsEsc = false;

        if (startPos != null)
        {
            Pos = startPos.Value;
            LineStart = Input.LastIndexOf("\n", startPos.Value - 1, StringComparison.Ordinal) + 1;
            CurLine = Whitespace.LineBreak.Split(Input.Substring(0, LineStart)).Length;
        }
        else
        {
            Pos = LineStart = 0;
            CurLine = 1;
        }

        Type = tt.Eof;
        Value = null;
        Start = End = Pos;
        StartLoc = EndLoc = CurPosition();

        LastTokEndLoc = LastTokStartLoc = null;
        LastTokStart = LastTokEnd = Pos;

        Context = InitialContext();
        ExprAllowed = true;

        InModule = options.SourceType == "module";
        Strict = InModule || options.Strict == true || StrictDirective(Pos);

        PotentialArrowAt = -1;
        PotentialArrowInForAwait = false;

        YieldPos = AwaitPos = AwaitIdentPos = 0;
        Labels = new List<LabelInfo>();
        UndefinedExports = new Dictionary<string, Node>();

        if (Pos == 0 && options.AllowHashBang == true && Input.Length >= 2 && Input.Substring(0, 2) == "#!")
            SkipLineComment(2);

        ScopeStack = new List<Scope>();
        EnterScope(Options.SourceType == "commonjs" ? SCOPE_FUNCTION : SCOPE_TOP);

        RegexpState = null;
        PrivateNameStack = new List<PrivateNameStatus>();
    }

    public Node Parse()
    {
        Node node = Options.Program ?? StartNode();
        NextToken();
        return CatchStackOverflow(() => ParseTopLevel(node));
    }

    public bool InFunction => (CurrentVarScope().Flags & SCOPE_FUNCTION) > 0;

    public bool InGenerator => (CurrentVarScope().Flags & SCOPE_GENERATOR) > 0;

    public bool InAsync => (CurrentVarScope().Flags & SCOPE_ASYNC) > 0;

    public bool CanAwait
    {
        get
        {
            for (int i = ScopeStack.Count - 1; i >= 0; i--)
            {
                int flags = ScopeStack[i].Flags;
                if ((flags & (SCOPE_CLASS_STATIC_BLOCK | SCOPE_CLASS_FIELD_INIT)) != 0) return false;
                if ((flags & SCOPE_FUNCTION) != 0) return (flags & SCOPE_ASYNC) > 0;
            }
            return (InModule && Options.EcmaVersion >= 13) || Options.AllowAwaitOutsideFunction == true;
        }
    }

    public bool AllowReturn
    {
        get
        {
            if (InFunction) return true;
            if (Options.AllowReturnOutsideFunction && (CurrentVarScope().Flags & SCOPE_TOP) != 0) return true;
            return false;
        }
    }

    public bool AllowSuper
    {
        get
        {
            int flags = CurrentThisScope().Flags;
            return (flags & SCOPE_SUPER) > 0 || Options.AllowSuperOutsideMethod == true;
        }
    }

    public bool AllowDirectSuper => (CurrentThisScope().Flags & SCOPE_DIRECT_SUPER) > 0;

    public bool TreatFunctionsAsVar => TreatFunctionsAsVarInScope(CurrentScope());

    public bool AllowNewDotTarget
    {
        get
        {
            for (int i = ScopeStack.Count - 1; i >= 0; i--)
            {
                int flags = ScopeStack[i].Flags;
                if ((flags & (SCOPE_CLASS_STATIC_BLOCK | SCOPE_CLASS_FIELD_INIT)) != 0 ||
                    ((flags & SCOPE_FUNCTION) != 0 && (flags & SCOPE_ARROW) == 0)) return true;
            }
            return false;
        }
    }

    public bool AllowUsing
    {
        get
        {
            int flags = CurrentScope().Flags;
            if ((flags & SCOPE_SWITCH) != 0) return false;
            if (!InModule && (flags & SCOPE_TOP) != 0) return false;
            return true;
        }
    }

    public bool InClassStaticBlock => (CurrentVarScope().Flags & SCOPE_CLASS_STATIC_BLOCK) > 0;

    public static Node ParseStatic(string input, Options? options) => new Parser(options, input).Parse();

    public static Node ParseExpressionAt(string input, int pos, Options? options)
    {
        var parser = new Parser(options, input, pos);
        parser.NextToken();
        return parser.ParseExpression();
    }

    public static Parser Tokenizer(string input, Options? options) => new Parser(options, input);
}

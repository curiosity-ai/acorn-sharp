namespace Acorn;

/// <summary>
/// A fine-grained token type carrying the information the tokenizer knows about
/// a token so the parser can look it up cheaply.
/// </summary>
public sealed class TokenType : IPropertyBag
{
    public string Label;
    public string? Keyword;
    public bool BeforeExpr;
    public bool StartsExpr;
    public bool IsLoop;
    public bool IsAssign;
    public bool Prefix;
    public bool Postfix;
    public int? Binop;
    /// <summary>Set later by the token-context module; takes (parser, prevType).</summary>
    public Action<Parser, TokenType>? UpdateContext;

    public bool TryGetProperty(string key, out object? value)
    {
        switch (key)
        {
            case "label": value = Label; return true;
            case "keyword": value = Keyword; return true;
            case "beforeExpr": value = BeforeExpr; return true;
            case "startsExpr": value = StartsExpr; return true;
            case "isLoop": value = IsLoop; return true;
            case "isAssign": value = IsAssign; return true;
            case "prefix": value = Prefix; return true;
            case "postfix": value = Postfix; return true;
            case "binop": value = Binop; return true;
            default: value = null; return false;
        }
    }

    public TokenType(string label, TokenTypeConfig? conf = null)
    {
        conf ??= default;
        Label = label;
        Keyword = conf.Value.Keyword;
        BeforeExpr = conf.Value.BeforeExpr;
        StartsExpr = conf.Value.StartsExpr;
        IsLoop = conf.Value.IsLoop;
        IsAssign = conf.Value.IsAssign;
        Prefix = conf.Value.Prefix;
        Postfix = conf.Value.Postfix;
        Binop = conf.Value.Binop;
    }

    public override string ToString() => Label;
}

/// <summary>Configuration for constructing a <see cref="TokenType"/>.</summary>
public struct TokenTypeConfig
{
    public string? Keyword;
    public bool BeforeExpr;
    public bool StartsExpr;
    public bool IsLoop;
    public bool IsAssign;
    public bool Prefix;
    public bool Postfix;
    public int? Binop;
}

/// <summary>
/// The set of token types. Referenced from the parser via the alias
/// <c>using tt = Acorn.TokenTypes;</c> so the code reads like the JS original
/// (<c>tt.Name</c>, <c>tt.ParenL</c>, <c>tt.Break</c>, ...).
/// </summary>
public static class TokenTypes
{
    /// <summary>Maps keyword names to their token types.</summary>
    public static readonly Dictionary<string, TokenType> Keywords = new();

    private static TokenType Binop(string name, int prec) =>
        new(name, new TokenTypeConfig { BeforeExpr = true, Binop = prec });

    private static TokenType Kw(string name, TokenTypeConfig options = default)
    {
        options.Keyword = name;
        var t = new TokenType(name, options);
        Keywords[name] = t;
        return t;
    }

    private static readonly TokenTypeConfig BeforeExprCfg = new() { BeforeExpr = true };
    private static readonly TokenTypeConfig StartsExprCfg = new() { StartsExpr = true };

    public static readonly TokenType Num = new("num", StartsExprCfg);
    public static readonly TokenType Regexp = new("regexp", StartsExprCfg);
    public static readonly TokenType String = new("string", StartsExprCfg);
    public static readonly TokenType Name = new("name", StartsExprCfg);
    public static readonly TokenType PrivateId = new("privateId", StartsExprCfg);
    public static readonly TokenType Eof = new("eof");

    // Punctuation token types.
    public static readonly TokenType BracketL = new("[", new TokenTypeConfig { BeforeExpr = true, StartsExpr = true });
    public static readonly TokenType BracketR = new("]");
    public static readonly TokenType BraceL = new("{", new TokenTypeConfig { BeforeExpr = true, StartsExpr = true });
    public static readonly TokenType BraceR = new("}");
    public static readonly TokenType ParenL = new("(", new TokenTypeConfig { BeforeExpr = true, StartsExpr = true });
    public static readonly TokenType ParenR = new(")");
    public static readonly TokenType Comma = new(",", BeforeExprCfg);
    public static readonly TokenType Semi = new(";", BeforeExprCfg);
    public static readonly TokenType Colon = new(":", BeforeExprCfg);
    public static readonly TokenType Dot = new(".");
    public static readonly TokenType Question = new("?", BeforeExprCfg);
    public static readonly TokenType QuestionDot = new("?.");
    public static readonly TokenType Arrow = new("=>", BeforeExprCfg);
    public static readonly TokenType Template = new("template");
    public static readonly TokenType InvalidTemplate = new("invalidTemplate");
    public static readonly TokenType Ellipsis = new("...", BeforeExprCfg);
    public static readonly TokenType BackQuote = new("`", StartsExprCfg);
    public static readonly TokenType DollarBraceL = new("${", new TokenTypeConfig { BeforeExpr = true, StartsExpr = true });

    // Operators.
    public static readonly TokenType Eq = new("=", new TokenTypeConfig { BeforeExpr = true, IsAssign = true });
    public static readonly TokenType Assign = new("_=", new TokenTypeConfig { BeforeExpr = true, IsAssign = true });
    public static readonly TokenType IncDec = new("++/--", new TokenTypeConfig { Prefix = true, Postfix = true, StartsExpr = true });
    public static readonly TokenType Prefix = new("!/~", new TokenTypeConfig { BeforeExpr = true, Prefix = true, StartsExpr = true });
    public static readonly TokenType LogicalOR = Binop("||", 1);
    public static readonly TokenType LogicalAND = Binop("&&", 2);
    public static readonly TokenType BitwiseOR = Binop("|", 3);
    public static readonly TokenType BitwiseXOR = Binop("^", 4);
    public static readonly TokenType BitwiseAND = Binop("&", 5);
    public static readonly TokenType Equality = Binop("==/!=/===/!==", 6);
    public static readonly TokenType Relational = Binop("</>/<=/>=", 7);
    public static readonly TokenType BitShift = Binop("<</>>/>>>", 8);
    public static readonly TokenType PlusMin = new("+/-", new TokenTypeConfig { BeforeExpr = true, Binop = 9, Prefix = true, StartsExpr = true });
    public static readonly TokenType Modulo = Binop("%", 10);
    public static readonly TokenType Star = Binop("*", 10);
    public static readonly TokenType Slash = Binop("/", 10);
    public static readonly TokenType StarStar = new("**", new TokenTypeConfig { BeforeExpr = true });
    public static readonly TokenType Coalesce = Binop("??", 1);

    // Keyword token types.
    public static readonly TokenType Break = Kw("break");
    public static readonly TokenType Case = Kw("case", BeforeExprCfg);
    public static readonly TokenType Catch = Kw("catch");
    public static readonly TokenType Continue = Kw("continue");
    public static readonly TokenType Debugger = Kw("debugger");
    public static readonly TokenType Default = Kw("default", BeforeExprCfg);
    public static readonly TokenType Do = Kw("do", new TokenTypeConfig { IsLoop = true, BeforeExpr = true });
    public static readonly TokenType Else = Kw("else", BeforeExprCfg);
    public static readonly TokenType Finally = Kw("finally");
    public static readonly TokenType For = Kw("for", new TokenTypeConfig { IsLoop = true });
    public static readonly TokenType Function = Kw("function", StartsExprCfg);
    public static readonly TokenType If = Kw("if");
    public static readonly TokenType Return = Kw("return", BeforeExprCfg);
    public static readonly TokenType Switch = Kw("switch");
    public static readonly TokenType Throw = Kw("throw", BeforeExprCfg);
    public static readonly TokenType Try = Kw("try");
    public static readonly TokenType Var = Kw("var");
    public static readonly TokenType Const = Kw("const");
    public static readonly TokenType While = Kw("while", new TokenTypeConfig { IsLoop = true });
    public static readonly TokenType With = Kw("with");
    public static readonly TokenType New = Kw("new", new TokenTypeConfig { BeforeExpr = true, StartsExpr = true });
    public static readonly TokenType This = Kw("this", StartsExprCfg);
    public static readonly TokenType Super = Kw("super", StartsExprCfg);
    public static readonly TokenType Class = Kw("class", StartsExprCfg);
    public static readonly TokenType Extends = Kw("extends", BeforeExprCfg);
    public static readonly TokenType Export = Kw("export");
    public static readonly TokenType Import = Kw("import", StartsExprCfg);
    public static readonly TokenType Null = Kw("null", StartsExprCfg);
    public static readonly TokenType True = Kw("true", StartsExprCfg);
    public static readonly TokenType False = Kw("false", StartsExprCfg);
    public static readonly TokenType In = Kw("in", new TokenTypeConfig { BeforeExpr = true, Binop = 7 });
    public static readonly TokenType InstanceOf = Kw("instanceof", new TokenTypeConfig { BeforeExpr = true, Binop = 7 });
    public static readonly TokenType TypeOf = Kw("typeof", new TokenTypeConfig { BeforeExpr = true, Prefix = true, StartsExpr = true });
    public static readonly TokenType Void = Kw("void", new TokenTypeConfig { BeforeExpr = true, Prefix = true, StartsExpr = true });
    public static readonly TokenType Delete = Kw("delete", new TokenTypeConfig { BeforeExpr = true, Prefix = true, StartsExpr = true });
}

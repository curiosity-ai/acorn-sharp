using tt = Acorn.TokenTypes;

namespace Acorn;

/// <summary>
/// Superficially tracks syntactic context to predict whether a regular
/// expression is allowed at a given point (loosely based on sweet.js).
/// </summary>
public sealed class TokContext
{
    public string Token;
    public bool IsExpr;
    public bool PreserveSpace;
    public Action<Parser>? Override;
    public bool Generator;

    public TokContext(string token, bool isExpr, bool preserveSpace = false,
        Action<Parser>? override_ = null, bool generator = false)
    {
        Token = token;
        IsExpr = isExpr;
        PreserveSpace = preserveSpace;
        Override = override_;
        Generator = generator;
    }
}

/// <summary>
/// The set of token contexts, referenced via <c>using tc = Acorn.TokContexts;</c>.
/// The static constructor also wires up the per-token <c>updateContext</c> hooks.
/// </summary>
public static class TokContexts
{
    public static readonly TokContext BStat = new("{", false);
    public static readonly TokContext BExpr = new("{", true);
    public static readonly TokContext BTmpl = new("${", false);
    public static readonly TokContext PStat = new("(", false);
    public static readonly TokContext PExpr = new("(", true);
    public static readonly TokContext QTmpl = new("`", true, true, p => p.TryReadTemplateToken());
    public static readonly TokContext FStat = new("function", false);
    public static readonly TokContext FExpr = new("function", true);
    public static readonly TokContext FExprGen = new("function", true, false, null, true);
    public static readonly TokContext FGen = new("function", false, false, null, true);

    static TokContexts()
    {
        // Token-specific context update code (tokencontext.js).
        tt.ParenR.UpdateContext = tt.BraceR.UpdateContext = (p, _) =>
        {
            if (p.Context.Count == 1) { p.ExprAllowed = true; return; }
            var outCtx = p.PopContext();
            if (outCtx == BStat && p.CurContext().Token == "function")
                outCtx = p.PopContext();
            p.ExprAllowed = !outCtx.IsExpr;
        };

        tt.BraceL.UpdateContext = (p, prevType) =>
        {
            p.PushContext(p.BraceIsBlock(prevType) ? BStat : BExpr);
            p.ExprAllowed = true;
        };

        tt.DollarBraceL.UpdateContext = (p, _) =>
        {
            p.PushContext(BTmpl);
            p.ExprAllowed = true;
        };

        tt.ParenL.UpdateContext = (p, prevType) =>
        {
            bool statementParens = prevType == tt.If || prevType == tt.For || prevType == tt.With || prevType == tt.While;
            p.PushContext(statementParens ? PStat : PExpr);
            p.ExprAllowed = true;
        };

        tt.IncDec.UpdateContext = (p, prevType) =>
        {
            // tokExprAllowed stays unchanged
        };

        tt.Function.UpdateContext = tt.Class.UpdateContext = (p, prevType) =>
        {
            if (prevType.BeforeExpr && prevType != tt.Else &&
                !(prevType == tt.Semi && p.CurContext() != PStat) &&
                !(prevType == tt.Return && Whitespace.LineBreak.IsMatch(p.Input.Substring(p.LastTokEnd, p.Start - p.LastTokEnd))) &&
                !((prevType == tt.Colon || prevType == tt.BraceL) && p.CurContext() == BStat))
                p.PushContext(FExpr);
            else
                p.PushContext(FStat);
            p.ExprAllowed = false;
        };

        tt.Colon.UpdateContext = (p, _) =>
        {
            if (p.CurContext().Token == "function") p.PopContext();
            p.ExprAllowed = true;
        };

        tt.BackQuote.UpdateContext = (p, _) =>
        {
            if (p.CurContext() == QTmpl)
                p.PopContext();
            else
                p.PushContext(QTmpl);
            p.ExprAllowed = false;
        };

        tt.Star.UpdateContext = (p, prevType) =>
        {
            if (prevType == tt.Function)
            {
                int index = p.Context.Count - 1;
                if (p.Context[index] == FExpr)
                    p.Context[index] = FExprGen;
                else
                    p.Context[index] = FGen;
            }
            p.ExprAllowed = true;
        };

        tt.Name.UpdateContext = (p, prevType) =>
        {
            bool allowed = false;
            if (p.Options.EcmaVersion >= 6 && prevType != tt.Dot)
            {
                if ((p.Value as string) == "of" && !p.ExprAllowed ||
                    (p.Value as string) == "yield" && p.InGeneratorContext())
                    allowed = true;
            }
            p.ExprAllowed = allowed;
        };
    }

    /// <summary>Forces the static constructor to run (wiring updateContext hooks).</summary>
    public static void EnsureInitialized() { }
}

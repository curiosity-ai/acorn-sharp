using tt = Acorn.TokenTypes;
using tc = Acorn.TokContexts;

namespace Acorn;

public partial class Parser
{
    public List<TokContext> InitialContext() => new List<TokContext> { tc.BStat };

    public TokContext CurContext() => Context[Context.Count - 1];

    public void PushContext(TokContext c) => Context.Add(c);

    public TokContext PopContext()
    {
        var c = Context[Context.Count - 1];
        Context.RemoveAt(Context.Count - 1);
        return c;
    }

    public bool BraceIsBlock(TokenType prevType)
    {
        TokContext parent = CurContext();
        if (parent == tc.FExpr || parent == tc.FStat)
            return true;
        if (prevType == tt.Colon && (parent == tc.BStat || parent == tc.BExpr))
            return !parent.IsExpr;

        // The check for `tt.name && exprAllowed` detects whether we are after a
        // `yield` or `of` construct. See the `updateContext` for `tt.name`.
        if (prevType == tt.Return || prevType == tt.Name && ExprAllowed)
            return Whitespace.LineBreak.IsMatch(Input.Substring(LastTokEnd, Start - LastTokEnd));
        if (prevType == tt.Else || prevType == tt.Semi || prevType == tt.Eof || prevType == tt.ParenR || prevType == tt.Arrow)
            return true;
        if (prevType == tt.BraceL)
            return parent == tc.BStat;
        if (prevType == tt.Var || prevType == tt.Const || prevType == tt.Name)
            return false;
        return !ExprAllowed;
    }

    public bool InGeneratorContext()
    {
        for (int i = Context.Count - 1; i >= 1; i--)
        {
            TokContext context = Context[i];
            if (context.Token == "function")
                return context.Generator;
        }
        return false;
    }

    public void UpdateContext(TokenType prevType)
    {
        TokenType type = Type;
        if (type.Keyword != null && prevType == tt.Dot)
            ExprAllowed = false;
        else if (type.UpdateContext != null)
            type.UpdateContext(this, prevType);
        else
            ExprAllowed = type.BeforeExpr;
    }

    // Used to handle edge cases when token context could not be inferred correctly.
    public void OverrideContext(TokContext tokenCtx)
    {
        if (CurContext() != tokenCtx)
            Context[Context.Count - 1] = tokenCtx;
    }
}

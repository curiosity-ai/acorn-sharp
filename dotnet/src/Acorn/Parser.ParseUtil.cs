using System.Text.RegularExpressions;
using tt = Acorn.TokenTypes;

namespace Acorn;

public partial class Parser
{
    // ## Parser utilities

    private static readonly Regex LiteralDirective =
        new("^(?:'((?:\\\\[\\s\\S]|[^'\\\\])*?)'|\"((?:\\\\[\\s\\S]|[^\"\\\\])*?)\")", RegexOptions.Compiled);

    private static readonly Regex DirectiveNextChar =
        new("[(`.\\[+\\-/*%<>=,?^&]", RegexOptions.Compiled);

    public bool StrictDirective(int start)
    {
        if (Options.EcmaVersion < 5) return false;
        for (; ; )
        {
            // Try to find string literal.
            var ws = Whitespace.SkipWhiteSpace.Match(Input, start);
            start += ws.Length;
            var match = LiteralDirective.Match(Input.Substring(start));
            if (!match.Success) return false;
            string content = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (content == "use strict")
            {
                var spaceAfter = Whitespace.SkipWhiteSpace.Match(Input, start + match.Length);
                int end = spaceAfter.Index + spaceAfter.Length;
                char next = end < Input.Length ? Input[end] : '\0';
                return next == ';' || next == '}' ||
                    (Whitespace.LineBreak.IsMatch(spaceAfter.Value) &&
                     !(DirectiveNextChar.IsMatch(next.ToString()) || (next == '!' && (end + 1 < Input.Length ? Input[end + 1] : '\0') == '=')));
            }
            start += match.Length;

            // Skip semicolon, if any.
            var ws2 = Whitespace.SkipWhiteSpace.Match(Input, start);
            start += ws2.Length;
            if (start < Input.Length && Input[start] == ';')
                start++;
        }
    }

    // Predicate that tests whether the next token is of the given type, and if
    // yes, consumes it as a side effect.
    public bool Eat(TokenType type)
    {
        if (Type == type)
        {
            Next();
            return true;
        }
        return false;
    }

    // Tests whether parsed token is a contextual keyword.
    public bool IsContextual(string name) => Type == tt.Name && (Value as string) == name && !ContainsEsc;

    // Consumes contextual keyword if possible.
    public bool EatContextual(string name)
    {
        if (!IsContextual(name)) return false;
        Next();
        return true;
    }

    public Node CatchStackOverflow(Func<Node> f)
    {
        // .NET's stack-overflow is not catchable; rely on deep recursion being rare.
        return f();
    }

    // Asserts that following token is given contextual keyword.
    public void ExpectContextual(string name)
    {
        if (!EatContextual(name)) Unexpected();
    }

    // Test whether a semicolon can be inserted at the current position.
    public bool CanInsertSemicolon() =>
        Type == tt.Eof ||
        Type == tt.BraceR ||
        Whitespace.LineBreak.IsMatch(Input.Substring(LastTokEnd, Start - LastTokEnd));

    public bool InsertSemicolon()
    {
        if (CanInsertSemicolon())
        {
            Options.OnInsertedSemicolon?.Invoke(LastTokEnd, LastTokEndLoc);
            return true;
        }
        return false;
    }

    // Consume a semicolon, or, failing that, pretend that there is one.
    public void Semicolon()
    {
        if (!Eat(tt.Semi) && !InsertSemicolon()) Unexpected();
    }

    public bool AfterTrailingComma(TokenType tokType, bool notNext = false)
    {
        if (Type == tokType)
        {
            Options.OnTrailingComma?.Invoke(LastTokStart, LastTokStartLoc);
            if (!notNext) Next();
            return true;
        }
        return false;
    }

    // Expect a token of a given type. If found, consume it, otherwise raise.
    public void Expect(TokenType type)
    {
        if (!Eat(type)) Unexpected();
    }

    // Raise an unexpected token error.
    public void Unexpected(int? pos = null) => Raise(pos ?? Start, "Unexpected token");

    public void CheckPatternErrors(DestructuringErrors? refDestructuringErrors, bool isAssign)
    {
        if (refDestructuringErrors == null) return;
        if (refDestructuringErrors.TrailingComma > -1)
            RaiseRecoverable(refDestructuringErrors.TrailingComma, "Comma is not permitted after the rest element");
        int parens = isAssign ? refDestructuringErrors.ParenthesizedAssign : refDestructuringErrors.ParenthesizedBind;
        if (parens > -1) RaiseRecoverable(parens, isAssign ? "Assigning to rvalue" : "Parenthesized pattern");
    }

    public bool CheckExpressionErrors(DestructuringErrors? refDestructuringErrors, bool andThrow = false)
    {
        if (refDestructuringErrors == null) return false;
        int shorthandAssign = refDestructuringErrors.ShorthandAssign;
        int doubleProto = refDestructuringErrors.DoubleProto;
        if (!andThrow) return shorthandAssign >= 0 || doubleProto >= 0;
        if (shorthandAssign >= 0)
            Raise(shorthandAssign, "Shorthand property assignments are valid only in destructuring patterns");
        if (doubleProto >= 0)
            RaiseRecoverable(doubleProto, "Redefinition of __proto__ property");
        return false;
    }

    public void CheckYieldAwaitInDefaultParams()
    {
        if (YieldPos != 0 && (AwaitPos == 0 || YieldPos < AwaitPos))
            Raise(YieldPos, "Yield expression cannot be a default value");
        if (AwaitPos != 0)
            Raise(AwaitPos, "Await expression cannot be a default value");
    }

    public bool IsSimpleAssignTarget(Node expr)
    {
        if (expr.Type == "ParenthesizedExpression")
            return IsSimpleAssignTarget((Node)expr["expression"]!);
        return expr.Type == "Identifier" || expr.Type == "MemberExpression";
    }
}

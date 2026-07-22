using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using tt = Acorn.TokenTypes;
using tc = Acorn.TokContexts;
using static Acorn.ScopeFlags;
using static Acorn.BindFlags;

namespace Acorn;

public partial class Parser
{
    // ## Shared helpers for faithful truthiness / stringification.

    private static bool Truthy(object? o) => o switch
    {
        null => false,
        bool b => b,
        string s => s.Length != 0,
        int i => i != 0,
        double d => d != 0,
        _ => true
    };

    private static bool ForInitTruthy(object? forInit) => forInit switch
    {
        null => false,
        bool b => b,
        string s => s.Length != 0,
        _ => true
    };

    private static string NumberToString(double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d) && Math.Abs(d) < 1e21)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string JsToString(object? v) => v switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        double d => NumberToString(d),
        BigInteger bi => bi.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    private static readonly Regex CrlfRegex = new("\r\n?", RegexOptions.Compiled);

    // Check if property name clashes with already added.
    // Object/class getters and setters are not allowed to clash —
    // either with each other or with an init property — and in
    // strict mode, init properties are also not allowed to be repeated.

    public void CheckPropClash(Node prop, Dictionary<string, object?> propHash, DestructuringErrors? refDestructuringErrors)
    {
        if (Options.EcmaVersion >= 9 && prop.Type == "SpreadElement")
            return;
        if (Options.EcmaVersion >= 6 && (Truthy(prop["computed"]) || Truthy(prop["method"]) || Truthy(prop["shorthand"])))
            return;
        Node key = (Node)prop["key"]!;
        string name;
        switch (key.Type)
        {
            case "Identifier": name = (string)key["name"]!; break;
            case "Literal": name = JsToString(key["value"]); break;
            default: return;
        }
        string? kind = (string?)prop["kind"];
        if (Options.EcmaVersion >= 6)
        {
            if (name == "__proto__" && kind == "init")
            {
                if (propHash.TryGetValue("proto", out var pv) && Truthy(pv))
                {
                    if (refDestructuringErrors != null)
                    {
                        if (refDestructuringErrors.DoubleProto < 0)
                        {
                            refDestructuringErrors.DoubleProto = key.Start;
                        }
                    }
                    else
                    {
                        RaiseRecoverable(key.Start, "Redefinition of __proto__ property");
                    }
                }
                propHash["proto"] = true;
            }
            return;
        }
        name = "$" + name;
        Dictionary<string, bool>? other = propHash.TryGetValue(name, out var ov) ? (Dictionary<string, bool>)ov! : null;
        if (other != null)
        {
            bool redefinition;
            if (kind == "init")
            {
                redefinition = Strict && other["init"] || other["get"] || other["set"];
            }
            else
            {
                redefinition = other["init"] || other[kind!];
            }
            if (redefinition)
                RaiseRecoverable(key.Start, "Redefinition of property");
        }
        else
        {
            other = new Dictionary<string, bool> { ["init"] = false, ["get"] = false, ["set"] = false };
            propHash[name] = other;
        }
        other[kind!] = true;
    }

    // ### Expression parsing

    // Parse a full expression.

    public Node ParseExpression(object? forInit = null, DestructuringErrors? refDestructuringErrors = null)
    {
        return CatchStackOverflow(() =>
        {
            int startPos = Start;
            Position? startLoc = StartLoc;
            Node expr = ParseMaybeAssign(forInit, refDestructuringErrors);
            if (Type == tt.Comma)
            {
                Node node = StartNodeAt(startPos, startLoc);
                var expressions = new List<object?> { expr };
                node["expressions"] = expressions;
                while (Eat(tt.Comma)) expressions.Add(ParseMaybeAssign(forInit, refDestructuringErrors));
                return FinishNode(node, "SequenceExpression");
            }
            return expr;
        });
    }

    // Parse an assignment expression. This includes applications of
    // operators like `+=`.

    public Node ParseMaybeAssign(object? forInit = null, DestructuringErrors? refDestructuringErrors = null, Func<Node, int, Position?, Node>? afterLeftParse = null)
    {
        if (IsContextual("yield"))
        {
            if (InGenerator) return ParseYield(forInit);
            // The tokenizer will assume an expression is allowed after
            // `yield`, but this isn't that kind of yield
            else ExprAllowed = false;
        }

        bool ownDestructuringErrors = false;
        int oldParenAssign = -1, oldTrailingComma = -1, oldDoubleProto = -1;
        if (refDestructuringErrors != null)
        {
            oldParenAssign = refDestructuringErrors.ParenthesizedAssign;
            oldTrailingComma = refDestructuringErrors.TrailingComma;
            oldDoubleProto = refDestructuringErrors.DoubleProto;
            refDestructuringErrors.ParenthesizedAssign = refDestructuringErrors.TrailingComma = -1;
        }
        else
        {
            refDestructuringErrors = new DestructuringErrors();
            ownDestructuringErrors = true;
        }

        int startPos = Start;
        Position? startLoc = StartLoc;
        if (Type == tt.ParenL || Type == tt.Name)
        {
            PotentialArrowAt = Start;
            PotentialArrowInForAwait = (forInit as string) == "await";
        }
        Node left = ParseMaybeConditional(forInit, refDestructuringErrors);
        if (afterLeftParse != null) left = afterLeftParse(left, startPos, startLoc);
        if (Type.IsAssign)
        {
            Node node = StartNodeAt(startPos, startLoc);
            node["operator"] = Value;
            if (Type == tt.Eq)
                left = ToAssignable(left, false, refDestructuringErrors)!;
            if (!ownDestructuringErrors)
            {
                refDestructuringErrors.ParenthesizedAssign = refDestructuringErrors.TrailingComma = refDestructuringErrors.DoubleProto = -1;
            }
            if (refDestructuringErrors.ShorthandAssign >= left.Start)
                refDestructuringErrors.ShorthandAssign = -1; // reset because shorthand default was used correctly
            if (Type == tt.Eq)
                CheckLValPattern(left);
            else
                CheckLValSimple(left);
            node["left"] = left;
            Next();
            node["right"] = ParseMaybeAssign(forInit);
            if (oldDoubleProto > -1) refDestructuringErrors.DoubleProto = oldDoubleProto;
            return FinishNode(node, "AssignmentExpression");
        }
        else
        {
            if (ownDestructuringErrors) CheckExpressionErrors(refDestructuringErrors, true);
        }
        if (oldParenAssign > -1) refDestructuringErrors.ParenthesizedAssign = oldParenAssign;
        if (oldTrailingComma > -1) refDestructuringErrors.TrailingComma = oldTrailingComma;
        return left;
    }

    // Parse a ternary conditional (`?:`) operator.

    public Node ParseMaybeConditional(object? forInit, DestructuringErrors? refDestructuringErrors)
    {
        int startPos = Start;
        Position? startLoc = StartLoc;
        Node expr = ParseExprOps(forInit, refDestructuringErrors);
        if (CheckExpressionErrors(refDestructuringErrors)) return expr;
        if (!(expr.Type == "ArrowFunctionExpression" && expr.Start == startPos) && Eat(tt.Question))
        {
            Node node = StartNodeAt(startPos, startLoc);
            node["test"] = expr;
            node["consequent"] = ParseMaybeAssign();
            Expect(tt.Colon);
            node["alternate"] = ParseMaybeAssign(forInit);
            return FinishNode(node, "ConditionalExpression");
        }
        return expr;
    }

    // Start the precedence parser.

    public Node ParseExprOps(object? forInit, DestructuringErrors? refDestructuringErrors)
    {
        int startPos = Start;
        Position? startLoc = StartLoc;
        Node expr = ParseMaybeUnary(refDestructuringErrors, false, false, forInit);
        if (CheckExpressionErrors(refDestructuringErrors)) return expr;
        return expr.Start == startPos && expr.Type == "ArrowFunctionExpression" ? expr : ParseExprOp(expr, startPos, startLoc, -1, forInit);
    }

    // Parse binary operators with the operator precedence parsing algorithm.

    public Node ParseExprOp(Node left, int leftStartPos, Position? leftStartLoc, int minPrec, object? forInit)
    {
        int? prec = Type.Binop;
        if (prec != null && (!ForInitTruthy(forInit) || Type != tt.In))
        {
            int precVal = prec.Value;
            if (precVal > minPrec)
            {
                bool logical = Type == tt.LogicalOR || Type == tt.LogicalAND;
                bool coalesce = Type == tt.Coalesce;
                if (coalesce)
                {
                    // Handle the precedence of `tt.coalesce` as equal to the range of logical expressions.
                    precVal = tt.LogicalAND.Binop!.Value;
                }
                object? op = Value;
                Next();
                int startPos = Start;
                Position? startLoc = StartLoc;
                Node right = ParseExprOp(ParseMaybeUnary(null, false, false, forInit), startPos, startLoc, precVal, forInit);
                Node node = BuildBinary(leftStartPos, leftStartLoc, left, right, op, logical || coalesce);
                if ((logical && Type == tt.Coalesce) || (coalesce && (Type == tt.LogicalOR || Type == tt.LogicalAND)))
                {
                    RaiseRecoverable(Start, "Logical expressions and coalesce expressions cannot be mixed. Wrap either by parentheses");
                }
                return ParseExprOp(node, leftStartPos, leftStartLoc, minPrec, forInit);
            }
        }
        return left;
    }

    public Node BuildBinary(int startPos, Position? startLoc, Node left, Node right, object? op, bool logical)
    {
        if (right.Type == "PrivateIdentifier") Raise(right.Start, "Private identifier can only be left side of binary expression");
        Node node = StartNodeAt(startPos, startLoc);
        node["left"] = left;
        node["operator"] = op;
        node["right"] = right;
        return FinishNode(node, logical ? "LogicalExpression" : "BinaryExpression");
    }

    // Parse unary operators, both prefix and postfix.

    public Node ParseMaybeUnary(DestructuringErrors? refDestructuringErrors, bool sawUnary, bool incDec, object? forInit)
    {
        int startPos = Start;
        Position? startLoc = StartLoc;
        Node expr;
        if (IsContextual("await") && CanAwait)
        {
            expr = ParseAwait(forInit);
            sawUnary = true;
        }
        else if (Type.Prefix)
        {
            Node node = StartNode();
            bool update = Type == tt.IncDec;
            node["operator"] = Value;
            node["prefix"] = true;
            Next();
            node["argument"] = ParseMaybeUnary(null, true, update, forInit);
            CheckExpressionErrors(refDestructuringErrors, true);
            if (update) CheckLValSimple((Node)node["argument"]!);
            else if (Strict && (string?)node["operator"] == "delete" && IsLocalVariableAccess((Node)node["argument"]!))
                RaiseRecoverable(node.Start, "Deleting local variable in strict mode");
            else if ((string?)node["operator"] == "delete" && IsPrivateFieldAccess((Node)node["argument"]!))
                RaiseRecoverable(node.Start, "Private fields can not be deleted");
            else sawUnary = true;
            expr = FinishNode(node, update ? "UpdateExpression" : "UnaryExpression");
        }
        else if (!sawUnary && Type == tt.PrivateId)
        {
            if ((ForInitTruthy(forInit) || PrivateNameStack.Count == 0) && Options.CheckPrivateFields) Unexpected();
            expr = ParsePrivateIdent();
            // only could be private fields in 'in', such as #x in obj
            if (Type != tt.In) Unexpected();
        }
        else
        {
            expr = ParseExprSubscripts(refDestructuringErrors, forInit);
            if (CheckExpressionErrors(refDestructuringErrors)) return expr;
            while (Type.Postfix && !CanInsertSemicolon())
            {
                Node node = StartNodeAt(startPos, startLoc);
                node["operator"] = Value;
                node["prefix"] = false;
                node["argument"] = expr;
                CheckLValSimple(expr);
                Next();
                expr = FinishNode(node, "UpdateExpression");
            }
        }

        if (!incDec && Eat(tt.StarStar))
        {
            if (sawUnary) { Unexpected(LastTokStart); return expr; }
            else
                return BuildBinary(startPos, startLoc, expr, ParseMaybeUnary(null, false, false, forInit), "**", false);
        }
        else
        {
            return expr;
        }
    }

    private static bool IsLocalVariableAccess(Node node)
    {
        return (
            node.Type == "Identifier" ||
            node.Type == "ParenthesizedExpression" && IsLocalVariableAccess((Node)node["expression"]!)
        );
    }

    private static bool IsPrivateFieldAccess(Node node)
    {
        return (
            node.Type == "MemberExpression" && ((Node)node["property"]!).Type == "PrivateIdentifier" ||
            node.Type == "ChainExpression" && IsPrivateFieldAccess((Node)node["expression"]!) ||
            node.Type == "ParenthesizedExpression" && IsPrivateFieldAccess((Node)node["expression"]!)
        );
    }

    // Parse call, dot, and `[]`-subscript expressions.

    public Node ParseExprSubscripts(DestructuringErrors? refDestructuringErrors = null, object? forInit = null)
    {
        int startPos = Start;
        Position? startLoc = StartLoc;
        Node expr = ParseExprAtom(refDestructuringErrors, forInit);
        if (expr.Type == "ArrowFunctionExpression" && Input.Substring(LastTokStart, LastTokEnd - LastTokStart) != ")")
            return expr;
        Node result = ParseSubscripts(expr, startPos, startLoc, false, forInit);
        if (refDestructuringErrors != null && result.Type == "MemberExpression")
        {
            if (refDestructuringErrors.ParenthesizedAssign >= result.Start) refDestructuringErrors.ParenthesizedAssign = -1;
            if (refDestructuringErrors.ParenthesizedBind >= result.Start) refDestructuringErrors.ParenthesizedBind = -1;
            if (refDestructuringErrors.TrailingComma >= result.Start) refDestructuringErrors.TrailingComma = -1;
        }
        return result;
    }

    public Node ParseSubscripts(Node baseNode, int startPos, Position? startLoc, bool noCalls, object? forInit)
    {
        bool maybeAsyncArrow = Options.EcmaVersion >= 8 && baseNode.Type == "Identifier" && (string?)baseNode["name"] == "async" &&
            LastTokEnd == baseNode.End && !CanInsertSemicolon() && baseNode.End - baseNode.Start == 5 &&
            PotentialArrowAt == baseNode.Start;
        bool optionalChained = false;

        while (true)
        {
            Node element = ParseSubscript(baseNode, startPos, startLoc, noCalls, maybeAsyncArrow, optionalChained, forInit);

            if (element["optional"] is bool eob && eob) optionalChained = true;
            if (element == baseNode || element.Type == "ArrowFunctionExpression")
            {
                if (optionalChained)
                {
                    Node chainNode = StartNodeAt(startPos, startLoc);
                    chainNode["expression"] = element;
                    element = FinishNode(chainNode, "ChainExpression");
                }
                return element;
            }

            baseNode = element;
        }
    }

    public bool ShouldParseAsyncArrow()
    {
        return !CanInsertSemicolon() && Eat(tt.Arrow);
    }

    public Node ParseSubscriptAsyncArrow(int startPos, Position? startLoc, List<object?> exprList, object? forInit)
    {
        return ParseArrowExpression(StartNodeAt(startPos, startLoc), exprList, true, forInit);
    }

    public Node ParseSubscript(Node baseNode, int startPos, Position? startLoc, bool noCalls, bool maybeAsyncArrow, bool optionalChained, object? forInit)
    {
        bool optionalSupported = Options.EcmaVersion >= 11;
        bool optional = optionalSupported && Eat(tt.QuestionDot);
        if (noCalls && optional) Raise(LastTokStart, "Optional chaining cannot appear in the callee of new expressions");

        bool computed = Eat(tt.BracketL);
        if (computed || (optional && Type != tt.ParenL && Type != tt.BackQuote) || Eat(tt.Dot))
        {
            Node node = StartNodeAt(startPos, startLoc);
            node["object"] = baseNode;
            if (computed)
            {
                node["property"] = ParseExpression();
                Expect(tt.BracketR);
            }
            else if (Type == tt.PrivateId && baseNode.Type != "Super")
            {
                node["property"] = ParsePrivateIdent();
            }
            else
            {
                node["property"] = ParseIdent(Options.AllowReserved != AllowReservedOption.Never);
            }
            node["computed"] = computed;
            if (optionalSupported)
            {
                node["optional"] = optional;
            }
            baseNode = FinishNode(node, "MemberExpression");
        }
        else if (!noCalls && Eat(tt.ParenL))
        {
            var refDestructuringErrors = new DestructuringErrors();
            int oldYieldPos = YieldPos, oldAwaitPos = AwaitPos, oldAwaitIdentPos = AwaitIdentPos;
            YieldPos = 0;
            AwaitPos = 0;
            AwaitIdentPos = 0;
            var exprList = ParseExprList(tt.ParenR, Options.EcmaVersion >= 8, false, refDestructuringErrors);
            if (maybeAsyncArrow && !optional && ShouldParseAsyncArrow())
            {
                CheckPatternErrors(refDestructuringErrors, false);
                CheckYieldAwaitInDefaultParams();
                if (AwaitIdentPos > 0)
                    Raise(AwaitIdentPos, "Cannot use 'await' as identifier inside an async function");
                YieldPos = oldYieldPos;
                AwaitPos = oldAwaitPos;
                AwaitIdentPos = oldAwaitIdentPos;
                return ParseSubscriptAsyncArrow(startPos, startLoc, exprList, forInit);
            }
            CheckExpressionErrors(refDestructuringErrors, true);
            YieldPos = oldYieldPos != 0 ? oldYieldPos : YieldPos;
            AwaitPos = oldAwaitPos != 0 ? oldAwaitPos : AwaitPos;
            AwaitIdentPos = oldAwaitIdentPos != 0 ? oldAwaitIdentPos : AwaitIdentPos;
            Node node = StartNodeAt(startPos, startLoc);
            node["callee"] = baseNode;
            node["arguments"] = exprList;
            if (optionalSupported)
            {
                node["optional"] = optional;
            }
            baseNode = FinishNode(node, "CallExpression");
        }
        else if (Type == tt.BackQuote)
        {
            if (optional || optionalChained)
            {
                Raise(Start, "Optional chaining cannot appear in the tag of tagged template expressions");
            }
            Node node = StartNodeAt(startPos, startLoc);
            node["tag"] = baseNode;
            node["quasi"] = ParseTemplate(true);
            baseNode = FinishNode(node, "TaggedTemplateExpression");
        }
        return baseNode;
    }

    // Parse an atomic expression.

    // Guards against runaway recursion on pathologically nested input. .NET's
    // StackOverflowException is uncatchable, so (unlike acorn's catchStackOverflow
    // which catches the engine's RangeError) we bound the recursion explicitly and
    // raise the same error acorn does.
    private int _exprRecursionDepth;
    private const int MaxExprRecursionDepth = 1800;

    public Node ParseExprAtom(DestructuringErrors? refDestructuringErrors = null, object? forInit = null, bool forNew = false)
    {
        if (++_exprRecursionDepth > MaxExprRecursionDepth)
        {
            _exprRecursionDepth--;
            Raise(Start, "Not enough stack space to parse input");
        }
        try
        {
            return ParseExprAtomCore(refDestructuringErrors, forInit, forNew);
        }
        finally
        {
            _exprRecursionDepth--;
        }
    }

    private Node ParseExprAtomCore(DestructuringErrors? refDestructuringErrors = null, object? forInit = null, bool forNew = false)
    {
        // If a division operator appears in an expression position, the
        // tokenizer got confused, and we force it to read a regexp instead.
        if (Type == tt.Slash) ReadRegexp();

        Node node;
        bool canBeArrow = PotentialArrowAt == Start;
        if (Type == tt.Super)
        {
            if (!AllowSuper)
                Raise(Start, "'super' keyword outside a method");
            node = StartNode();
            Next();
            if (Type == tt.ParenL && !AllowDirectSuper)
                Raise(node.Start, "super() call outside constructor of a subclass");
            // The `super` keyword can appear as SuperProperty or SuperCall.
            if (Type != tt.Dot && Type != tt.BracketL && Type != tt.ParenL)
                Unexpected();
            return FinishNode(node, "Super");
        }
        if (Type == tt.This)
        {
            node = StartNode();
            Next();
            return FinishNode(node, "ThisExpression");
        }
        if (Type == tt.Name)
        {
            int startPos = Start;
            Position? startLoc = StartLoc;
            bool containsEsc = ContainsEsc;
            Node id = ParseIdent(false);
            if (Options.EcmaVersion >= 8 && !containsEsc && (string?)id["name"] == "async" && !CanInsertSemicolon() && Eat(tt.Function))
            {
                OverrideContext(tc.FExpr);
                return ParseFunction(StartNodeAt(startPos, startLoc), 0, false, true, forInit);
            }
            if (canBeArrow && !CanInsertSemicolon())
            {
                if (Eat(tt.Arrow))
                    return ParseArrowExpression(StartNodeAt(startPos, startLoc), new List<object?> { id }, false, forInit);
                if (Options.EcmaVersion >= 8 && (string?)id["name"] == "async" && Type == tt.Name && !containsEsc &&
                    (!PotentialArrowInForAwait || (string?)Value != "of" || ContainsEsc))
                {
                    id = ParseIdent(false);
                    if (CanInsertSemicolon() || !Eat(tt.Arrow))
                        Unexpected();
                    return ParseArrowExpression(StartNodeAt(startPos, startLoc), new List<object?> { id }, true, forInit);
                }
            }
            return id;
        }
        if (Type == tt.Regexp)
        {
            var value = (RegexpTokenValue)Value!;
            node = ParseLiteral(value.Value);
            node["regex"] = new Dictionary<string, object?> { ["pattern"] = value.Pattern, ["flags"] = value.Flags };
            return node;
        }
        if (Type == tt.Num || Type == tt.String)
        {
            return ParseLiteral(Value);
        }
        if (Type == tt.Null || Type == tt.True || Type == tt.False)
        {
            node = StartNode();
            node["value"] = Type == tt.Null ? (object?)null : (Type == tt.True);
            node["raw"] = Type.Keyword;
            Next();
            return FinishNode(node, "Literal");
        }
        if (Type == tt.ParenL)
        {
            int start = Start;
            Node expr = ParseParenAndDistinguishExpression(canBeArrow, forInit);
            if (refDestructuringErrors != null)
            {
                if (refDestructuringErrors.ParenthesizedAssign < 0 && !IsSimpleAssignTarget(expr))
                    refDestructuringErrors.ParenthesizedAssign = start;
                if (refDestructuringErrors.ParenthesizedBind < 0)
                    refDestructuringErrors.ParenthesizedBind = start;
            }
            return expr;
        }
        if (Type == tt.BracketL)
        {
            node = StartNode();
            Next();
            node["elements"] = ParseExprList(tt.BracketR, true, true, refDestructuringErrors);
            return FinishNode(node, "ArrayExpression");
        }
        if (Type == tt.BraceL)
        {
            OverrideContext(tc.BExpr);
            return ParseObj(false, refDestructuringErrors);
        }
        if (Type == tt.Function)
        {
            node = StartNode();
            Next();
            return ParseFunction(node, 0);
        }
        if (Type == tt.Class)
        {
            return ParseClass(StartNode(), false);
        }
        if (Type == tt.New)
        {
            return ParseNew();
        }
        if (Type == tt.BackQuote)
        {
            return ParseTemplate();
        }
        if (Type == tt.Import)
        {
            if (Options.EcmaVersion >= 11)
            {
                return ParseExprImport(forNew);
            }
            else
            {
                Unexpected();
                return null!;
            }
        }
        return ParseExprAtomDefault();
    }

    public Node ParseExprAtomDefault()
    {
        Unexpected();
        return null!;
    }

    public Node ParseExprImport(bool forNew)
    {
        Node node = StartNode();

        // Consume `import` as an identifier for `import.meta`.
        if (ContainsEsc) RaiseRecoverable(Start, "Escape sequence in keyword import");
        Next();

        if (Type == tt.ParenL && !forNew)
        {
            return ParseDynamicImport(node);
        }
        else if (Type == tt.Dot)
        {
            Node meta = StartNodeAt(node.Start, node.Loc?.Start);
            meta["name"] = "import";
            node["meta"] = FinishNode(meta, "Identifier");
            return ParseImportMeta(node);
        }
        else
        {
            Unexpected();
            return null!;
        }
    }

    public Node ParseDynamicImport(Node node)
    {
        Next(); // skip `(`

        // Parse node.source.
        node["source"] = ParseMaybeAssign();

        if (Options.EcmaVersion >= 16)
        {
            if (!Eat(tt.ParenR))
            {
                Expect(tt.Comma);
                if (!AfterTrailingComma(tt.ParenR))
                {
                    node["options"] = ParseMaybeAssign();
                    if (!Eat(tt.ParenR))
                    {
                        Expect(tt.Comma);
                        if (!AfterTrailingComma(tt.ParenR))
                        {
                            Unexpected();
                        }
                    }
                }
                else
                {
                    node["options"] = null;
                }
            }
            else
            {
                node["options"] = null;
            }
        }
        else
        {
            // Verify ending.
            if (!Eat(tt.ParenR))
            {
                int errorPos = Start;
                if (Eat(tt.Comma) && Eat(tt.ParenR))
                {
                    RaiseRecoverable(errorPos, "Trailing comma is not allowed in import()");
                }
                else
                {
                    Unexpected(errorPos);
                }
            }
        }

        return FinishNode(node, "ImportExpression");
    }

    public Node ParseImportMeta(Node node)
    {
        Next(); // skip `.`

        bool containsEsc = ContainsEsc;
        node["property"] = ParseIdent(true);

        if ((string?)((Node)node["property"]!)["name"] != "meta")
            RaiseRecoverable(((Node)node["property"]!).Start, "The only valid meta property for import is 'import.meta'");
        if (containsEsc)
            RaiseRecoverable(node.Start, "'import.meta' must not contain escaped characters");
        if (Options.SourceType != "module" && !Options.AllowImportExportEverywhere)
            RaiseRecoverable(node.Start, "Cannot use 'import.meta' outside a module");

        return FinishNode(node, "MetaProperty");
    }

    public Node ParseLiteral(object? value)
    {
        Node node = StartNode();
        node["value"] = value;
        node["raw"] = Input.Substring(Start, End - Start);
        string raw = (string)node["raw"]!;
        if (raw.Length > 0 && raw[raw.Length - 1] == 'n')
            node["bigint"] = node["value"] != null ? node["value"]!.ToString()! : raw.Substring(0, raw.Length - 1).Replace("_", "");
        Next();
        return FinishNode(node, "Literal");
    }

    public Node ParseParenExpression()
    {
        Expect(tt.ParenL);
        Node val = ParseExpression();
        Expect(tt.ParenR);
        return val;
    }

    public bool ShouldParseArrow(List<object?> exprList)
    {
        return !CanInsertSemicolon();
    }

    public Node ParseParenAndDistinguishExpression(bool canBeArrow, object? forInit)
    {
        int startPos = Start;
        Position? startLoc = StartLoc;
        Node val;
        bool allowTrailingComma = Options.EcmaVersion >= 8;
        if (Options.EcmaVersion >= 6)
        {
            Next();

            int innerStartPos = Start;
            Position? innerStartLoc = StartLoc;
            var exprList = new List<object?>();
            bool first = true, lastIsComma = false;
            var refDestructuringErrors = new DestructuringErrors();
            int oldYieldPos = YieldPos, oldAwaitPos = AwaitPos;
            int? spreadStart = null;
            YieldPos = 0;
            AwaitPos = 0;
            // Do not save awaitIdentPos to allow checking awaits nested in parameters
            while (Type != tt.ParenR)
            {
                if (first) first = false; else Expect(tt.Comma);
                if (allowTrailingComma && AfterTrailingComma(tt.ParenR, true))
                {
                    lastIsComma = true;
                    break;
                }
                else if (Type == tt.Ellipsis)
                {
                    spreadStart = Start;
                    exprList.Add(ParseParenItem(ParseRestBinding()));
                    if (Type == tt.Comma)
                    {
                        RaiseRecoverable(
                            Start,
                            "Comma is not permitted after the rest element"
                        );
                    }
                    break;
                }
                else
                {
                    exprList.Add(ParseMaybeAssign(false, refDestructuringErrors, (item, sp, sl) => ParseParenItem(item)));
                }
            }
            int innerEndPos = LastTokEnd;
            Position? innerEndLoc = LastTokEndLoc;
            Expect(tt.ParenR);

            if (canBeArrow && ShouldParseArrow(exprList) && Eat(tt.Arrow))
            {
                CheckPatternErrors(refDestructuringErrors, false);
                CheckYieldAwaitInDefaultParams();
                YieldPos = oldYieldPos;
                AwaitPos = oldAwaitPos;
                return ParseParenArrowList(startPos, startLoc, exprList, forInit);
            }

            if (exprList.Count == 0 || lastIsComma) Unexpected(LastTokStart);
            if (spreadStart != null) Unexpected(spreadStart.Value);
            CheckExpressionErrors(refDestructuringErrors, true);
            YieldPos = oldYieldPos != 0 ? oldYieldPos : YieldPos;
            AwaitPos = oldAwaitPos != 0 ? oldAwaitPos : AwaitPos;

            if (exprList.Count > 1)
            {
                val = StartNodeAt(innerStartPos, innerStartLoc);
                val["expressions"] = exprList;
                FinishNodeAt(val, "SequenceExpression", innerEndPos, innerEndLoc);
            }
            else
            {
                val = (Node)exprList[0]!;
            }
        }
        else
        {
            val = ParseParenExpression();
        }

        if (Options.PreserveParens)
        {
            Node par = StartNodeAt(startPos, startLoc);
            par["expression"] = val;
            return FinishNode(par, "ParenthesizedExpression");
        }
        else
        {
            return val;
        }
    }

    public Node ParseParenItem(Node item)
    {
        return item;
    }

    public Node ParseParenArrowList(int startPos, Position? startLoc, List<object?> exprList, object? forInit)
    {
        return ParseArrowExpression(StartNodeAt(startPos, startLoc), exprList, false, forInit);
    }

    // New's precedence is slightly tricky.

    public Node ParseNew()
    {
        if (ContainsEsc) RaiseRecoverable(Start, "Escape sequence in keyword new");
        Node node = StartNode();
        Next();
        if (Options.EcmaVersion >= 6 && Type == tt.Dot)
        {
            Node meta = StartNodeAt(node.Start, node.Loc?.Start);
            meta["name"] = "new";
            node["meta"] = FinishNode(meta, "Identifier");
            Next();
            bool containsEsc = ContainsEsc;
            node["property"] = ParseIdent(true);
            if ((string?)((Node)node["property"]!)["name"] != "target")
                RaiseRecoverable(((Node)node["property"]!).Start, "The only valid meta property for new is 'new.target'");
            if (containsEsc)
                RaiseRecoverable(node.Start, "'new.target' must not contain escaped characters");
            if (!AllowNewDotTarget)
                RaiseRecoverable(node.Start, "'new.target' can only be used in functions and class static block");
            return FinishNode(node, "MetaProperty");
        }
        int startPos = Start;
        Position? startLoc = StartLoc;
        node["callee"] = ParseSubscripts(ParseExprAtom(null, false, true), startPos, startLoc, true, false);
        if (((Node)node["callee"]!).Type == "Super")
            RaiseRecoverable(startPos, "Invalid use of 'super'");
        if (Eat(tt.ParenL)) node["arguments"] = ParseExprList(tt.ParenR, Options.EcmaVersion >= 8, false);
        else node["arguments"] = new List<object?>();
        return FinishNode(node, "NewExpression");
    }

    // Parse template expression.

    public Node ParseTemplateElement(bool isTagged)
    {
        Node elem = StartNode();
        if (Type == tt.InvalidTemplate)
        {
            if (!isTagged)
            {
                RaiseRecoverable(Start, "Bad escape sequence in untagged template literal");
            }
            elem["value"] = new Dictionary<string, object?>
            {
                ["raw"] = CrlfRegex.Replace((string)Value!, "\n"),
                ["cooked"] = null
            };
        }
        else
        {
            elem["value"] = new Dictionary<string, object?>
            {
                ["raw"] = CrlfRegex.Replace(Input.Substring(Start, End - Start), "\n"),
                ["cooked"] = Value
            };
        }
        Next();
        elem["tail"] = Type == tt.BackQuote;
        return FinishNode(elem, "TemplateElement");
    }

    public Node ParseTemplate(bool isTagged = false)
    {
        Node node = StartNode();
        Next();
        var expressions = new List<object?>();
        node["expressions"] = expressions;
        Node curElt = ParseTemplateElement(isTagged);
        var quasis = new List<object?> { curElt };
        node["quasis"] = quasis;
        while (!(bool)curElt["tail"]!)
        {
            if (Type == tt.Eof) Raise(Pos, "Unterminated template literal");
            Expect(tt.DollarBraceL);
            expressions.Add(ParseExpression());
            Expect(tt.BraceR);
            quasis.Add(curElt = ParseTemplateElement(isTagged));
        }
        Next();
        return FinishNode(node, "TemplateLiteral");
    }

    public bool IsAsyncProp(Node prop)
    {
        Node key = (Node)prop["key"]!;
        return !Truthy(prop["computed"]) && key.Type == "Identifier" && (string?)key["name"] == "async" &&
            (Type == tt.Name || Type == tt.Num || Type == tt.String || Type == tt.BracketL || Type.Keyword != null || (Options.EcmaVersion >= 9 && Type == tt.Star)) &&
            !Whitespace.LineBreak.IsMatch(Input.Substring(LastTokEnd, Start - LastTokEnd));
    }

    // Parse an object literal or binding pattern.

    public Node ParseObj(bool isPattern, DestructuringErrors? refDestructuringErrors = null)
    {
        Node node = StartNode();
        bool first = true;
        var propHash = new Dictionary<string, object?>();
        var properties = new List<object?>();
        node["properties"] = properties;
        Next();
        while (!Eat(tt.BraceR))
        {
            if (!first)
            {
                Expect(tt.Comma);
                if (Options.EcmaVersion >= 5 && AfterTrailingComma(tt.BraceR)) break;
            }
            else first = false;

            Node prop = ParseProperty(isPattern, refDestructuringErrors);
            if (!isPattern) CheckPropClash(prop, propHash, refDestructuringErrors);
            properties.Add(prop);
        }
        return FinishNode(node, isPattern ? "ObjectPattern" : "ObjectExpression");
    }

    public Node ParseProperty(bool isPattern, DestructuringErrors? refDestructuringErrors)
    {
        Node prop = StartNode();
        bool isGenerator = false, isAsync = false;
        int startPos = 0;
        Position? startLoc = null;
        if (Options.EcmaVersion >= 9 && Eat(tt.Ellipsis))
        {
            if (isPattern)
            {
                prop["argument"] = ParseIdent(false);
                if (Type == tt.Comma)
                {
                    RaiseRecoverable(Start, "Comma is not permitted after the rest element");
                }
                return FinishNode(prop, "RestElement");
            }
            // Parse argument.
            prop["argument"] = ParseMaybeAssign(false, refDestructuringErrors);
            // To disallow trailing comma via `this.toAssignable()`.
            if (Type == tt.Comma && refDestructuringErrors != null && refDestructuringErrors.TrailingComma < 0)
            {
                refDestructuringErrors.TrailingComma = Start;
            }
            // Finish
            return FinishNode(prop, "SpreadElement");
        }
        if (Options.EcmaVersion >= 6)
        {
            prop["method"] = false;
            prop["shorthand"] = false;
            if (isPattern || refDestructuringErrors != null)
            {
                startPos = Start;
                startLoc = StartLoc;
            }
            if (!isPattern)
                isGenerator = Eat(tt.Star);
        }
        bool containsEsc = ContainsEsc;
        ParsePropertyName(prop);
        if (!isPattern && !containsEsc && Options.EcmaVersion >= 8 && !isGenerator && IsAsyncProp(prop))
        {
            isAsync = true;
            isGenerator = Options.EcmaVersion >= 9 && Eat(tt.Star);
            ParsePropertyName(prop);
        }
        else
        {
            isAsync = false;
        }
        ParsePropertyValue(prop, isPattern, isGenerator, isAsync, startPos, startLoc, refDestructuringErrors, containsEsc);
        return FinishNode(prop, "Property");
    }

    public void ParseGetterSetter(Node prop)
    {
        string kind = (string)((Node)prop["key"]!)["name"]!;
        ParsePropertyName(prop);
        prop["value"] = ParseMethod(false);
        prop["kind"] = kind;
        int paramCount = (string?)prop["kind"] == "get" ? 0 : 1;
        Node value = (Node)prop["value"]!;
        var valueParams = (List<object?>)value["params"]!;
        if (valueParams.Count != paramCount)
        {
            int start = value.Start;
            if ((string?)prop["kind"] == "get")
                RaiseRecoverable(start, "getter should have no params");
            else
                RaiseRecoverable(start, "setter should have exactly one param");
        }
        else
        {
            if ((string?)prop["kind"] == "set" && ((Node)valueParams[0]!).Type == "RestElement")
                RaiseRecoverable(((Node)valueParams[0]!).Start, "Setter cannot use rest params");
        }
    }

    public void ParsePropertyValue(Node prop, bool isPattern, bool isGenerator, bool isAsync, int startPos, Position? startLoc, DestructuringErrors? refDestructuringErrors, bool containsEsc)
    {
        if ((isGenerator || isAsync) && Type == tt.Colon)
            Unexpected();

        if (Eat(tt.Colon))
        {
            prop["value"] = isPattern ? ParseMaybeDefault(Start, StartLoc) : ParseMaybeAssign(false, refDestructuringErrors);
            prop["kind"] = "init";
        }
        else if (Options.EcmaVersion >= 6 && Type == tt.ParenL)
        {
            if (isPattern) Unexpected();
            prop["method"] = true;
            prop["value"] = ParseMethod(isGenerator, isAsync);
            prop["kind"] = "init";
        }
        else if (!isPattern && !containsEsc &&
                 Options.EcmaVersion >= 5 && !Truthy(prop["computed"]) && ((Node)prop["key"]!).Type == "Identifier" &&
                 ((string?)((Node)prop["key"]!)["name"] == "get" || (string?)((Node)prop["key"]!)["name"] == "set") &&
                 (Type != tt.Comma && Type != tt.BraceR && Type != tt.Eq))
        {
            if (isGenerator || isAsync) Unexpected();
            ParseGetterSetter(prop);
        }
        else if (Options.EcmaVersion >= 6 && !Truthy(prop["computed"]) && ((Node)prop["key"]!).Type == "Identifier")
        {
            if (isGenerator || isAsync) Unexpected();
            CheckUnreserved((Node)prop["key"]!);
            if ((string?)((Node)prop["key"]!)["name"] == "await" && AwaitIdentPos == 0)
                AwaitIdentPos = startPos;
            if (isPattern)
            {
                prop["value"] = ParseMaybeDefault(startPos, startLoc, CopyNode((Node)prop["key"]!));
            }
            else if (Type == tt.Eq && refDestructuringErrors != null)
            {
                if (refDestructuringErrors.ShorthandAssign < 0)
                    refDestructuringErrors.ShorthandAssign = Start;
                prop["value"] = ParseMaybeDefault(startPos, startLoc, CopyNode((Node)prop["key"]!));
            }
            else
            {
                prop["value"] = CopyNode((Node)prop["key"]!);
            }
            prop["kind"] = "init";
            prop["shorthand"] = true;
        }
        else Unexpected();
    }

    public Node ParsePropertyName(Node prop)
    {
        if (Options.EcmaVersion >= 6)
        {
            if (Eat(tt.BracketL))
            {
                prop["computed"] = true;
                prop["key"] = ParseMaybeAssign();
                Expect(tt.BracketR);
                return (Node)prop["key"]!;
            }
            else
            {
                prop["computed"] = false;
            }
        }
        prop["key"] = Type == tt.Num || Type == tt.String ? ParseExprAtom() : ParseIdent(Options.AllowReserved != AllowReservedOption.Never);
        return (Node)prop["key"]!;
    }

    // Initialize empty function node.

    public void InitFunction(Node node)
    {
        node["id"] = null;
        if (Options.EcmaVersion >= 6) { node["generator"] = false; node["expression"] = false; }
        if (Options.EcmaVersion >= 8) node["async"] = false;
    }

    // Parse object or class method.

    public Node ParseMethod(bool isGenerator, bool isAsync = false, bool allowDirectSuper = false)
    {
        Node node = StartNode();
        int oldYieldPos = YieldPos, oldAwaitPos = AwaitPos, oldAwaitIdentPos = AwaitIdentPos;

        InitFunction(node);
        if (Options.EcmaVersion >= 6)
            node["generator"] = isGenerator;
        if (Options.EcmaVersion >= 8)
            node["async"] = isAsync;

        YieldPos = 0;
        AwaitPos = 0;
        AwaitIdentPos = 0;
        EnterScope(FunctionFlags(isAsync, Truthy(node["generator"])) | SCOPE_SUPER | (allowDirectSuper ? SCOPE_DIRECT_SUPER : 0));

        Expect(tt.ParenL);
        node["params"] = ParseBindingList(tt.ParenR, false, Options.EcmaVersion >= 8);
        CheckYieldAwaitInDefaultParams();
        ParseFunctionBody(node, false, true, false);

        YieldPos = oldYieldPos;
        AwaitPos = oldAwaitPos;
        AwaitIdentPos = oldAwaitIdentPos;
        return FinishNode(node, "FunctionExpression");
    }

    // Parse arrow function expression with given parameters.

    public Node ParseArrowExpression(Node node, List<object?> parameters, bool isAsync, object? forInit = null)
    {
        int oldYieldPos = YieldPos, oldAwaitPos = AwaitPos, oldAwaitIdentPos = AwaitIdentPos;

        EnterScope(FunctionFlags(isAsync, false) | SCOPE_ARROW);
        InitFunction(node);
        if (Options.EcmaVersion >= 8) node["async"] = isAsync;

        YieldPos = 0;
        AwaitPos = 0;
        AwaitIdentPos = 0;

        node["params"] = ToAssignableList(parameters, true);
        ParseFunctionBody(node, true, false, forInit);

        YieldPos = oldYieldPos;
        AwaitPos = oldAwaitPos;
        AwaitIdentPos = oldAwaitIdentPos;
        return FinishNode(node, "ArrowFunctionExpression");
    }

    // Parse function body and check parameters.

    public void ParseFunctionBody(Node node, bool isArrowFunction, bool isMethod, object? forInit)
    {
        bool isExpression = isArrowFunction && Type != tt.BraceL;
        bool oldStrict = Strict, useStrict = false;

        if (isExpression)
        {
            node["body"] = ParseMaybeAssign(forInit);
            node["expression"] = true;
            CheckParams(node, false);
        }
        else
        {
            bool nonSimple = Options.EcmaVersion >= 7 && !IsSimpleParamList((List<object?>)node["params"]!);
            if (!oldStrict || nonSimple)
            {
                useStrict = StrictDirective(End);
                // If this is a strict mode function, verify that argument names
                // are not repeated, and it does not try to bind the words `eval`
                // or `arguments`.
                if (useStrict && nonSimple)
                    RaiseRecoverable(node.Start, "Illegal 'use strict' directive in function with non-simple parameter list");
            }
            // Start a new scope with regard to labels and the `inFunction`
            // flag (restore them to their old value afterwards).
            List<LabelInfo> oldLabels = Labels;
            Labels = new List<LabelInfo>();
            if (useStrict) Strict = true;

            // Add the params to varDeclaredNames to ensure that an error is thrown
            // if a let/const declaration in the function clashes with one of the params.
            CheckParams(node, !oldStrict && !useStrict && !isArrowFunction && !isMethod && IsSimpleParamList((List<object?>)node["params"]!));
            // Ensure the function name isn't a forbidden identifier in strict mode, e.g. 'eval'
            if (Strict && node["id"] != null) CheckLValSimple((Node)node["id"]!, BIND_OUTSIDE);
            node["body"] = ParseBlock(false, null, useStrict && !oldStrict);
            node["expression"] = false;
            AdaptDirectivePrologue((List<object?>)((Node)node["body"]!)["body"]!);
            Labels = oldLabels;
        }
        ExitScope();
    }

    public bool IsSimpleParamList(List<object?> parameters)
    {
        foreach (var param in parameters)
            if (((Node)param!).Type != "Identifier") return false;
        return true;
    }

    // Checks function params for various disallowed patterns.

    public void CheckParams(Node node, bool allowDuplicates)
    {
        var nameHash = new Dictionary<string, object?>();
        foreach (var param in (List<object?>)node["params"]!)
            CheckLValInnerPattern((Node)param!, BIND_VAR, allowDuplicates ? null : nameHash);
    }

    // Parses a comma-separated list of expressions.

    public List<object?> ParseExprList(TokenType close, bool allowTrailingComma, bool allowEmpty, DestructuringErrors? refDestructuringErrors = null)
    {
        var elts = new List<object?>();
        bool first = true;
        while (!Eat(close))
        {
            if (!first)
            {
                Expect(tt.Comma);
                if (allowTrailingComma && AfterTrailingComma(close)) break;
            }
            else first = false;

            Node? elt;
            if (allowEmpty && Type == tt.Comma)
                elt = null;
            else if (Type == tt.Ellipsis)
            {
                elt = ParseSpread(refDestructuringErrors);
                if (refDestructuringErrors != null && Type == tt.Comma && refDestructuringErrors.TrailingComma < 0)
                    refDestructuringErrors.TrailingComma = Start;
            }
            else
            {
                elt = ParseMaybeAssign(false, refDestructuringErrors);
            }
            elts.Add(elt);
        }
        return elts;
    }

    public void CheckUnreserved(Node node)
    {
        int start = node.Start, end = node.End;
        // For a string-literal export/import name the node has no `name`; JS
        // coerces `undefined` through the regex tests (which never match), so a
        // null name here must behave the same (no keyword/reserved match).
        string name = node["name"] as string ?? "";
        if (InGenerator && name == "yield")
            RaiseRecoverable(start, "Cannot use 'yield' as identifier inside a generator");
        if (InAsync && name == "await")
            RaiseRecoverable(start, "Cannot use 'await' as identifier inside an async function");
        if ((CurrentThisScope().Flags & SCOPE_VAR) == 0 && name == "arguments")
            RaiseRecoverable(start, "Cannot use 'arguments' in class field initializer");
        if (InClassStaticBlock && (name == "arguments" || name == "await"))
            Raise(start, $"Cannot use {name} in class static initialization block");
        if (Keywords.IsMatch(name))
            Raise(start, $"Unexpected keyword '{name}'");
        if (Options.EcmaVersion < 6 &&
            Input.Substring(start, end - start).IndexOf('\\') != -1) return;
        Regex re = Strict ? ReservedWordsStrict : ReservedWords;
        if (re.IsMatch(name))
        {
            if (!InAsync && name == "await")
                RaiseRecoverable(start, "Cannot use keyword 'await' outside an async function");
            RaiseRecoverable(start, $"The keyword '{name}' is reserved");
        }
    }

    // Parse the next token as an identifier.

    public Node ParseIdent(bool liberal = false)
    {
        Node node = ParseIdentNode();
        Next(liberal);
        FinishNode(node, "Identifier");
        if (!liberal)
        {
            CheckUnreserved(node);
            if ((string?)node["name"] == "await" && AwaitIdentPos == 0)
                AwaitIdentPos = node.Start;
        }
        return node;
    }

    public Node ParseIdentNode()
    {
        Node node = StartNode();
        if (Type == tt.Name)
        {
            node["name"] = (string)Value!;
        }
        else if (Type.Keyword != null)
        {
            node["name"] = Type.Keyword;

            // To fix https://github.com/acornjs/acorn/issues/575
            // `class` and `function` keywords push new context into this.context.
            // But there is no chance to pop the context if the keyword is consumed
            // as an identifier such as a property name. If the previous token is a
            // dot, this does not apply because the context-managing code already
            // ignored the keyword.
            if (((string?)node["name"] == "class" || (string?)node["name"] == "function") &&
                (LastTokEnd != LastTokStart + 1 || CharCodeAt(LastTokStart) != 46))
            {
                Context.RemoveAt(Context.Count - 1);
            }
            Type = tt.Name;
        }
        else
        {
            Unexpected();
        }
        return node;
    }

    public Node ParsePrivateIdent()
    {
        Node node = StartNode();
        if (Type == tt.PrivateId)
        {
            node["name"] = (string)Value!;
        }
        else
        {
            Unexpected();
        }
        Next();
        FinishNode(node, "PrivateIdentifier");

        // For validating existence
        if (Options.CheckPrivateFields)
        {
            if (PrivateNameStack.Count == 0)
            {
                Raise(node.Start, $"Private field '#{node["name"]}' must be declared in an enclosing class");
            }
            else
            {
                PrivateNameStack[PrivateNameStack.Count - 1].Used.Add(node);
            }
        }

        return node;
    }

    // Parses yield expression inside generator.

    public Node ParseYield(object? forInit)
    {
        if (YieldPos == 0) YieldPos = Start;

        Node node = StartNode();
        Next();
        if (Type == tt.Semi || CanInsertSemicolon() || (Type != tt.Star && !Type.StartsExpr))
        {
            node["delegate"] = false;
            node["argument"] = null;
        }
        else
        {
            node["delegate"] = Eat(tt.Star);
            node["argument"] = ParseMaybeAssign(forInit);
        }
        return FinishNode(node, "YieldExpression");
    }

    public Node ParseAwait(object? forInit)
    {
        if (AwaitPos == 0) AwaitPos = Start;

        Node node = StartNode();
        Next();
        node["argument"] = ParseMaybeUnary(null, true, false, forInit);
        return FinishNode(node, "AwaitExpression");
    }
}

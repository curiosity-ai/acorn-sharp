using System.Text.RegularExpressions;
using Acorn;
using tt = Acorn.TokenTypes;
using tc = Acorn.TokContexts;
using static Acorn.Loose.ParseUtil;

namespace Acorn.Loose;

// Ported from acorn-loose/src/expression.js
public partial class LooseParser
{
    private static readonly Regex LogicalOpRegex = new("&&|\\|\\||\\?\\?", RegexOptions.Compiled);
    private static readonly Regex CrLfRegex = new("\r\n?", RegexOptions.Compiled);
    private static readonly Regex NumericSeparatorRegex = new("_", RegexOptions.Compiled);

    public Node? CheckLVal(Node? expr)
    {
        if (expr == null) return expr;
        switch (expr.Type)
        {
            case "Identifier":
            case "MemberExpression":
                return expr;

            case "ParenthesizedExpression":
                expr["expression"] = this.CheckLVal((Node?)expr["expression"]);
                return expr;

            default:
                return this.DummyIdent();
        }
    }

    public Node ParseExpression(bool noIn = false)
    {
        StoredPos start = this.StoreCurrentPos();
        Node expr = this.ParseMaybeAssign(noIn);
        if (this.tok.Type == tt.Comma)
        {
            Node node = this.StartNodeAt(start);
            var expressions = new List<object?> { expr };
            node["expressions"] = expressions;
            while (this.Eat(tt.Comma)) expressions.Add(this.ParseMaybeAssign(noIn));
            return this.FinishNode(node, "SequenceExpression");
        }
        return expr;
    }

    public Node ParseParenExpression()
    {
        this.PushCx();
        this.Expect(tt.ParenL);
        Node val = this.ParseExpression();
        this.PopCx();
        this.Expect(tt.ParenR);
        return val;
    }

    public Node ParseMaybeAssign(bool noIn = false)
    {
        // `yield` should be an identifier reference if it's not in generator functions.
        if (this.inGenerator && this.toks.IsContextual("yield"))
        {
            Node node = this.StartNode();
            this.Next();
            if (this.Semicolon() || this.CanInsertSemicolon() || (this.tok.Type != tt.Star && !this.tok.Type.StartsExpr))
            {
                node["delegate"] = false;
                node["argument"] = null;
            }
            else
            {
                node["delegate"] = this.Eat(tt.Star);
                node["argument"] = this.ParseMaybeAssign();
            }
            return this.FinishNode(node, "YieldExpression");
        }

        StoredPos start = this.StoreCurrentPos();
        Node left = this.ParseMaybeConditional(noIn);
        if (this.tok.Type.IsAssign)
        {
            Node node = this.StartNodeAt(start);
            node["operator"] = this.tok.Value;
            node["left"] = this.tok.Type == tt.Eq ? this.ToAssignable(left) : this.CheckLVal(left);
            this.Next();
            node["right"] = this.ParseMaybeAssign(noIn);
            return this.FinishNode(node, "AssignmentExpression");
        }
        return left;
    }

    public Node ParseMaybeConditional(bool noIn = false)
    {
        StoredPos start = this.StoreCurrentPos();
        Node expr = this.ParseExprOps(noIn);
        if (this.Eat(tt.Question))
        {
            Node node = this.StartNodeAt(start);
            node["test"] = expr;
            node["consequent"] = this.ParseMaybeAssign();
            node["alternate"] = this.Expect(tt.Colon) ? this.ParseMaybeAssign(noIn) : this.DummyIdent();
            return this.FinishNode(node, "ConditionalExpression");
        }
        return expr;
    }

    public Node ParseExprOps(bool noIn = false)
    {
        StoredPos start = this.StoreCurrentPos();
        int indent = this.curIndent, line = this.curLineStart;
        return this.ParseExprOp(this.ParseMaybeUnary(false), start, -1, noIn, indent, line);
    }

    public Node ParseExprOp(Node left, StoredPos start, int minPrec, bool noIn, int indent, int line)
    {
        if (this.curLineStart != line && this.curIndent < indent && this.TokenStartsLine()) return left;
        int? prec = this.tok.Type.Binop;
        if (prec != null && (!noIn || this.tok.Type != tt.In))
        {
            if (prec > minPrec)
            {
                Node node = this.StartNodeAt(start);
                node["left"] = left;
                node["operator"] = this.tok.Value;
                this.Next();
                if (this.curLineStart != line && this.curIndent < indent && this.TokenStartsLine())
                {
                    node["right"] = this.DummyIdent();
                }
                else
                {
                    StoredPos rightStart = this.StoreCurrentPos();
                    node["right"] = this.ParseExprOp(this.ParseMaybeUnary(false), rightStart, prec.Value, noIn, indent, line);
                }
                this.FinishNode(node, LogicalOpRegex.IsMatch((string)node["operator"]!) ? "LogicalExpression" : "BinaryExpression");
                return this.ParseExprOp(node, start, minPrec, noIn, indent, line);
            }
        }
        return left;
    }

    public Node ParseMaybeUnary(bool sawUnary)
    {
        StoredPos start = this.StoreCurrentPos();
        Node expr;
        if (this.options.EcmaVersion >= 8 && this.toks.IsContextual("await") &&
            (this.inAsync || (this.toks.InModule && this.options.EcmaVersion >= 13) ||
             (!this.inFunction && this.options.AllowAwaitOutsideFunction == true)))
        {
            expr = this.ParseAwait();
            sawUnary = true;
        }
        else if (this.tok.Type.Prefix)
        {
            Node node = this.StartNode();
            bool update = this.tok.Type == tt.IncDec;
            if (!update) sawUnary = true;
            node["operator"] = this.tok.Value;
            node["prefix"] = true;
            this.Next();
            node["argument"] = this.ParseMaybeUnary(true);
            if (update) node["argument"] = this.CheckLVal((Node?)node["argument"]);
            expr = this.FinishNode(node, update ? "UpdateExpression" : "UnaryExpression");
        }
        else if (this.tok.Type == tt.Ellipsis)
        {
            Node node = this.StartNode();
            this.Next();
            node["argument"] = this.ParseMaybeUnary(sawUnary);
            expr = this.FinishNode(node, "SpreadElement");
        }
        else if (!sawUnary && this.tok.Type == tt.PrivateId)
        {
            expr = this.ParsePrivateIdent();
        }
        else
        {
            expr = this.ParseExprSubscripts();
            while (this.tok.Type.Postfix && !this.CanInsertSemicolon())
            {
                Node node = this.StartNodeAt(start);
                node["operator"] = this.tok.Value;
                node["prefix"] = false;
                node["argument"] = this.CheckLVal(expr);
                this.Next();
                expr = this.FinishNode(node, "UpdateExpression");
            }
        }

        if (!sawUnary && this.Eat(tt.StarStar))
        {
            Node node = this.StartNodeAt(start);
            node["operator"] = "**";
            node["left"] = expr;
            node["right"] = this.ParseMaybeUnary(false);
            return this.FinishNode(node, "BinaryExpression");
        }

        return expr;
    }

    public Node ParseExprSubscripts()
    {
        StoredPos start = this.StoreCurrentPos();
        return this.ParseSubscripts(this.ParseExprAtom(), start, false, this.curIndent, this.curLineStart);
    }

    public Node ParseSubscripts(Node baseExpr, StoredPos start, bool noCalls, int startIndent, int line)
    {
        bool optionalSupported = this.options.EcmaVersion >= 11;
        bool optionalChained = false;
        for (; ; )
        {
            if (this.curLineStart != line && this.curIndent <= startIndent && this.TokenStartsLine())
            {
                if (this.tok.Type == tt.Dot && this.curIndent == startIndent)
                    --startIndent;
                else
                    break;
            }

            bool maybeAsyncArrow = baseExpr.Type == "Identifier" && (baseExpr["name"] as string) == "async" && !this.CanInsertSemicolon();
            bool optional = optionalSupported && this.Eat(tt.QuestionDot);
            if (optional)
            {
                optionalChained = true;
            }

            if ((optional && this.tok.Type != tt.ParenL && this.tok.Type != tt.BracketL && this.tok.Type != tt.BackQuote) || this.Eat(tt.Dot))
            {
                Node node = this.StartNodeAt(start);
                node["object"] = baseExpr;
                if (this.curLineStart != line && this.curIndent <= startIndent && this.TokenStartsLine())
                    node["property"] = this.DummyIdent();
                else
                    node["property"] = this.ParsePropertyAccessor() ?? this.DummyIdent();
                node["computed"] = false;
                if (optionalSupported)
                    node["optional"] = optional;
                baseExpr = this.FinishNode(node, "MemberExpression");
            }
            else if (this.tok.Type == tt.BracketL)
            {
                this.PushCx();
                this.Next();
                Node node = this.StartNodeAt(start);
                node["object"] = baseExpr;
                node["property"] = this.ParseExpression();
                node["computed"] = true;
                if (optionalSupported)
                    node["optional"] = optional;
                this.PopCx();
                this.Expect(tt.BracketR);
                baseExpr = this.FinishNode(node, "MemberExpression");
            }
            else if (!noCalls && this.tok.Type == tt.ParenL)
            {
                var exprList = this.ParseExprList(tt.ParenR);
                if (maybeAsyncArrow && this.Eat(tt.Arrow))
                    return this.ParseArrowExpression(this.StartNodeAt(start), exprList, true);
                Node node = this.StartNodeAt(start);
                node["callee"] = baseExpr;
                node["arguments"] = exprList;
                if (optionalSupported)
                    node["optional"] = optional;
                baseExpr = this.FinishNode(node, "CallExpression");
            }
            else if (this.tok.Type == tt.BackQuote)
            {
                Node node = this.StartNodeAt(start);
                node["tag"] = baseExpr;
                node["quasi"] = this.ParseTemplate();
                baseExpr = this.FinishNode(node, "TaggedTemplateExpression");
            }
            else
            {
                break;
            }
        }

        if (optionalChained)
        {
            Node chainNode = this.StartNodeAt(start);
            chainNode["expression"] = baseExpr;
            baseExpr = this.FinishNode(chainNode, "ChainExpression");
        }
        return baseExpr;
    }

    public Node ParseExprAtom()
    {
        Node node;
        if (this.tok.Type == tt.This || this.tok.Type == tt.Super)
        {
            string type = this.tok.Type == tt.This ? "ThisExpression" : "Super";
            node = this.StartNode();
            this.Next();
            return this.FinishNode(node, type);
        }

        if (this.tok.Type == tt.Name)
        {
            StoredPos start = this.StoreCurrentPos();
            Node id = this.ParseIdent();
            bool isAsync = false;
            if ((id["name"] as string) == "async" && !this.CanInsertSemicolon())
            {
                if (this.Eat(tt.Function))
                {
                    this.toks.OverrideContext(tc.FExpr);
                    return this.ParseFunction(this.StartNodeAt(start), false, true);
                }
                if (this.tok.Type == tt.Name)
                {
                    id = this.ParseIdent();
                    isAsync = true;
                }
            }
            return this.Eat(tt.Arrow) ? this.ParseArrowExpression(this.StartNodeAt(start), new List<object?> { id }, isAsync) : id;
        }

        if (this.tok.Type == tt.Regexp)
        {
            node = this.StartNode();
            var val = (RegexpTokenValue)this.tok.Value!;
            node["regex"] = new Dictionary<string, object?> { ["pattern"] = val.Pattern, ["flags"] = val.Flags };
            node["value"] = val.Value;
            node["raw"] = Slice(this.input, this.tok.Start, this.tok.End);
            this.Next();
            return this.FinishNode(node, "Literal");
        }

        if (this.tok.Type == tt.Num || this.tok.Type == tt.String)
        {
            node = this.StartNode();
            node["value"] = this.tok.Value;
            string raw = Slice(this.input, this.tok.Start, this.tok.End);
            node["raw"] = raw;
            if (this.tok.Type == tt.Num && raw.Length > 0 && raw[raw.Length - 1] == 'n')
            {
                object? v = node["value"];
                node["bigint"] = v != null ? v.ToString() : NumericSeparatorRegex.Replace(raw.Substring(0, raw.Length - 1), "");
            }
            this.Next();
            return this.FinishNode(node, "Literal");
        }

        if (this.tok.Type == tt.Null || this.tok.Type == tt.True || this.tok.Type == tt.False)
        {
            node = this.StartNode();
            node["value"] = this.tok.Type == tt.Null ? null : (object)(this.tok.Type == tt.True);
            node["raw"] = this.tok.Type.Keyword;
            this.Next();
            return this.FinishNode(node, "Literal");
        }

        if (this.tok.Type == tt.ParenL)
        {
            StoredPos parenStart = this.StoreCurrentPos();
            this.Next();
            Node inner = this.ParseExpression();
            this.Expect(tt.ParenR);
            if (this.Eat(tt.Arrow))
            {
                // (a,)=>a  SequenceExpression makes dummy in the last hole. Drop the dummy.
                List<object?> pars = inner["expressions"] as List<object?> ?? new List<object?> { inner };
                if (pars.Count > 0 && IsDummy((Node?)pars[pars.Count - 1]))
                    pars.RemoveAt(pars.Count - 1);
                return this.ParseArrowExpression(this.StartNodeAt(parenStart), pars, false);
            }
            if (this.options.PreserveParens)
            {
                Node par = this.StartNodeAt(parenStart);
                par["expression"] = inner;
                inner = this.FinishNode(par, "ParenthesizedExpression");
            }
            return inner;
        }

        if (this.tok.Type == tt.BracketL)
        {
            node = this.StartNode();
            node["elements"] = this.ParseExprList(tt.BracketR, true);
            return this.FinishNode(node, "ArrayExpression");
        }

        if (this.tok.Type == tt.BraceL)
        {
            this.toks.OverrideContext(tc.BExpr);
            return this.ParseObj();
        }

        if (this.tok.Type == tt.Class)
        {
            return this.ParseClass(false);
        }

        if (this.tok.Type == tt.Function)
        {
            node = this.StartNode();
            this.Next();
            return this.ParseFunction(node, false);
        }

        if (this.tok.Type == tt.New)
        {
            return this.ParseNew();
        }

        if (this.tok.Type == tt.BackQuote)
        {
            return this.ParseTemplate();
        }

        if (this.tok.Type == tt.Import)
        {
            if (this.options.EcmaVersion >= 11)
                return this.ParseExprImport();
            else
                return this.DummyIdent();
        }

        return this.DummyIdent();
    }

    public Node ParseExprImport()
    {
        Node node = this.StartNode();
        Node meta = this.ParseIdent(true);
        if (this.tok.Type == tt.ParenL)
        {
            return this.ParseDynamicImport(node);
        }
        if (this.tok.Type == tt.Dot)
        {
            node["meta"] = meta;
            return this.ParseImportMeta(node);
        }
        node["name"] = "import";
        return this.FinishNode(node, "Identifier");
    }

    public Node ParseDynamicImport(Node node)
    {
        var list = this.ParseExprList(tt.ParenR);
        node["source"] = list.Count > 0 ? list[0] : this.DummyString();
        node["options"] = list.Count > 1 ? list[1] : null;
        return this.FinishNode(node, "ImportExpression");
    }

    public Node ParseImportMeta(Node node)
    {
        this.Next(); // skip '.'
        node["property"] = this.ParseIdent(true);
        return this.FinishNode(node, "MetaProperty");
    }

    public Node ParseNew()
    {
        Node node = this.StartNode();
        int startIndent = this.curIndent, line = this.curLineStart;
        Node meta = this.ParseIdent(true);
        if (this.options.EcmaVersion >= 6 && this.Eat(tt.Dot))
        {
            node["meta"] = meta;
            node["property"] = this.ParseIdent(true);
            return this.FinishNode(node, "MetaProperty");
        }
        StoredPos start = this.StoreCurrentPos();
        node["callee"] = this.ParseSubscripts(this.ParseExprAtom(), start, true, startIndent, line);
        if (this.tok.Type == tt.ParenL)
            node["arguments"] = this.ParseExprList(tt.ParenR);
        else
            node["arguments"] = new List<object?>();
        return this.FinishNode(node, "NewExpression");
    }

    public Node ParseTemplateElement()
    {
        Node elem = this.StartNode();

        // The loose parser accepts invalid unicode escapes even in untagged templates.
        if (this.tok.Type == tt.InvalidTemplate)
        {
            elem["value"] = new Dictionary<string, object?>
            {
                ["raw"] = this.tok.Value,
                ["cooked"] = null
            };
        }
        else
        {
            elem["value"] = new Dictionary<string, object?>
            {
                ["raw"] = CrLfRegex.Replace(Slice(this.input, this.tok.Start, this.tok.End), "\n"),
                ["cooked"] = this.tok.Value
            };
        }
        this.Next();
        elem["tail"] = this.tok.Type == tt.BackQuote;
        return this.FinishNode(elem, "TemplateElement");
    }

    public Node ParseTemplate()
    {
        Node node = this.StartNode();
        this.Next();
        node["expressions"] = new List<object?>();
        Node curElt = this.ParseTemplateElement();
        var quasis = new List<object?> { curElt };
        node["quasis"] = quasis;
        while (!(bool)curElt["tail"]!)
        {
            this.Next();
            ((List<object?>)node["expressions"]!).Add(this.ParseExpression());
            if (this.Expect(tt.BraceR))
            {
                curElt = this.ParseTemplateElement();
            }
            else
            {
                curElt = this.StartNode();
                curElt["value"] = new Dictionary<string, object?> { ["cooked"] = "", ["raw"] = "" };
                curElt["tail"] = true;
                this.FinishNode(curElt, "TemplateElement");
            }
            quasis.Add(curElt);
        }
        this.Expect(tt.BackQuote);
        return this.FinishNode(node, "TemplateLiteral");
    }

    public Node ParseObj()
    {
        Node node = this.StartNode();
        var properties = new List<object?>();
        node["properties"] = properties;
        this.PushCx();
        int indent = this.curIndent + 1, line = this.curLineStart;
        this.Eat(tt.BraceL);
        if (this.curIndent + 1 < indent) { indent = this.curIndent; line = this.curLineStart; }
        while (!this.Closes(tt.BraceR, indent, line))
        {
            Node prop = this.StartNode();
            bool isGenerator = false, isAsync = false;
            StoredPos start = default;
            if (this.options.EcmaVersion >= 9 && this.Eat(tt.Ellipsis))
            {
                prop["argument"] = this.ParseMaybeAssign();
                properties.Add(this.FinishNode(prop, "SpreadElement"));
                this.Eat(tt.Comma);
                continue;
            }
            if (this.options.EcmaVersion >= 6)
            {
                start = this.StoreCurrentPos();
                prop["method"] = false;
                prop["shorthand"] = false;
                isGenerator = this.Eat(tt.Star);
            }
            this.ParsePropertyName(prop);
            if (this.toks.IsAsyncProp(prop))
            {
                isAsync = true;
                isGenerator = this.options.EcmaVersion >= 9 && this.Eat(tt.Star);
                this.ParsePropertyName(prop);
            }
            else
            {
                isAsync = false;
            }
            if (IsDummy((Node)prop["key"]!))
            {
                if (IsDummy(this.ParseMaybeAssign())) this.Next();
                this.Eat(tt.Comma);
                continue;
            }
            if (this.Eat(tt.Colon))
            {
                prop["kind"] = "init";
                prop["value"] = this.ParseMaybeAssign();
            }
            else if (this.options.EcmaVersion >= 6 && (this.tok.Type == tt.ParenL || this.tok.Type == tt.BraceL))
            {
                prop["kind"] = "init";
                prop["method"] = true;
                prop["value"] = this.ParseMethod(isGenerator, isAsync);
            }
            else if (this.options.EcmaVersion >= 5 && ((Node)prop["key"]!).Type == "Identifier" &&
                     !(prop["computed"] as bool? ?? false) && ((((Node)prop["key"]!)["name"] as string) is "get" or "set") &&
                     (this.tok.Type != tt.Comma && this.tok.Type != tt.BraceR && this.tok.Type != tt.Eq))
            {
                prop["kind"] = ((Node)prop["key"]!)["name"];
                this.ParsePropertyName(prop);
                prop["value"] = this.ParseMethod(false);
            }
            else
            {
                prop["kind"] = "init";
                if (this.options.EcmaVersion >= 6)
                {
                    if (this.Eat(tt.Eq))
                    {
                        Node assign = this.StartNodeAt(start);
                        assign["operator"] = "=";
                        assign["left"] = prop["key"];
                        assign["right"] = this.ParseMaybeAssign();
                        prop["value"] = this.FinishNode(assign, "AssignmentExpression");
                    }
                    else
                    {
                        prop["value"] = prop["key"];
                    }
                }
                else
                {
                    prop["value"] = this.DummyIdent();
                }
                prop["shorthand"] = true;
            }
            properties.Add(this.FinishNode(prop, "Property"));
            this.Eat(tt.Comma);
        }
        this.PopCx();
        if (!this.Eat(tt.BraceR))
        {
            // If there is no closing brace, make the node span to the start of the next token.
            this.last.End = this.tok.Start;
            if (this.options.Locations) this.last.Loc!.End = this.tok.Loc!.Start;
        }
        return this.FinishNode(node, "ObjectExpression");
    }

    public void ParsePropertyName(Node prop)
    {
        if (this.options.EcmaVersion >= 6)
        {
            if (this.Eat(tt.BracketL))
            {
                prop["computed"] = true;
                prop["key"] = this.ParseExpression();
                this.Expect(tt.BracketR);
                return;
            }
            else
            {
                prop["computed"] = false;
            }
        }
        Node? key = (this.tok.Type == tt.Num || this.tok.Type == tt.String) ? this.ParseExprAtom() : this.ParseIdent();
        prop["key"] = key ?? this.DummyIdent();
    }

    public Node? ParsePropertyAccessor()
    {
        if (this.tok.Type == tt.Name || this.tok.Type.Keyword != null) return this.ParseIdent();
        if (this.tok.Type == tt.PrivateId) return this.ParsePrivateIdent();
        return null;
    }

    public Node ParseIdent(bool liberal = false)
    {
        string? name = this.tok.Type == tt.Name ? (this.tok.Value as string) : this.tok.Type.Keyword;
        if (string.IsNullOrEmpty(name)) return this.DummyIdent();
        if (this.tok.Type.Keyword != null) this.toks.Type = tt.Name;
        Node node = this.StartNode();
        this.Next();
        node["name"] = name;
        return this.FinishNode(node, "Identifier");
    }

    public Node ParsePrivateIdent()
    {
        Node node = this.StartNode();
        node["name"] = this.tok.Value;
        this.Next();
        return this.FinishNode(node, "PrivateIdentifier");
    }

    public void InitFunction(Node node)
    {
        node["id"] = null;
        node["params"] = new List<object?>();
        if (this.options.EcmaVersion >= 6)
        {
            node["generator"] = false;
            node["expression"] = false;
        }
        if (this.options.EcmaVersion >= 8)
            node["async"] = false;
    }

    // Convert existing expression atom to assignable pattern if possible.
    public Node? ToAssignable(Node? node, bool binding = false)
    {
        if (node == null || node.Type == "Identifier" || (node.Type == "MemberExpression" && !binding))
        {
            // Okay
        }
        else if (node.Type == "ParenthesizedExpression")
        {
            this.ToAssignable((Node?)node["expression"], binding);
        }
        else if (this.options.EcmaVersion < 6)
        {
            return this.DummyIdent();
        }
        else if (node.Type == "ObjectExpression")
        {
            node.Type = "ObjectPattern";
            foreach (var prop in (List<object?>)node["properties"]!)
                this.ToAssignable((Node?)prop, binding);
        }
        else if (node.Type == "ArrayExpression")
        {
            node.Type = "ArrayPattern";
            this.ToAssignableList((List<object?>)node["elements"]!, binding);
        }
        else if (node.Type == "Property")
        {
            this.ToAssignable((Node?)node["value"], binding);
        }
        else if (node.Type == "SpreadElement")
        {
            node.Type = "RestElement";
            this.ToAssignable((Node?)node["argument"], binding);
        }
        else if (node.Type == "AssignmentExpression")
        {
            node.Type = "AssignmentPattern";
            node.Remove("operator");
        }
        else
        {
            return this.DummyIdent();
        }
        return node;
    }

    public List<object?> ToAssignableList(List<object?> exprList, bool binding = false)
    {
        foreach (var expr in exprList)
            this.ToAssignable((Node?)expr, binding);
        return exprList;
    }

    public List<object?> ParseFunctionParams()
    {
        var parameters = this.ParseExprList(tt.ParenR);
        return this.ToAssignableList(parameters, true);
    }

    public Node ParseMethod(bool isGenerator, bool isAsync = false)
    {
        Node node = this.StartNode();
        bool oldInAsync = this.inAsync, oldInGenerator = this.inGenerator, oldInFunction = this.inFunction;
        this.InitFunction(node);
        if (this.options.EcmaVersion >= 6)
            node["generator"] = isGenerator;
        if (this.options.EcmaVersion >= 8)
            node["async"] = isAsync;
        this.inAsync = node["async"] as bool? ?? false;
        this.inGenerator = node["generator"] as bool? ?? false;
        this.inFunction = true;
        node["params"] = this.ParseFunctionParams();
        node["body"] = this.ParseBlock();
        this.toks.AdaptDirectivePrologue((List<object?>)((Node)node["body"]!)["body"]!);
        this.inAsync = oldInAsync;
        this.inGenerator = oldInGenerator;
        this.inFunction = oldInFunction;
        return this.FinishNode(node, "FunctionExpression");
    }

    public Node ParseArrowExpression(Node node, List<object?> parameters, bool isAsync = false)
    {
        bool oldInAsync = this.inAsync, oldInGenerator = this.inGenerator, oldInFunction = this.inFunction;
        this.InitFunction(node);
        if (this.options.EcmaVersion >= 8)
            node["async"] = isAsync;
        this.inAsync = node["async"] as bool? ?? false;
        this.inGenerator = false;
        this.inFunction = true;
        node["params"] = this.ToAssignableList(parameters, true);
        node["expression"] = this.tok.Type != tt.BraceL;
        if ((bool)node["expression"]!)
        {
            node["body"] = this.ParseMaybeAssign();
        }
        else
        {
            node["body"] = this.ParseBlock();
            this.toks.AdaptDirectivePrologue((List<object?>)((Node)node["body"]!)["body"]!);
        }
        this.inAsync = oldInAsync;
        this.inGenerator = oldInGenerator;
        this.inFunction = oldInFunction;
        return this.FinishNode(node, "ArrowFunctionExpression");
    }

    public List<object?> ParseExprList(TokenType close, bool allowEmpty = false)
    {
        this.PushCx();
        int indent = this.curIndent, line = this.curLineStart;
        var elts = new List<object?>();
        this.Next(); // Opening bracket
        while (!this.Closes(close, indent + 1, line))
        {
            if (this.Eat(tt.Comma))
            {
                elts.Add(allowEmpty ? null : this.DummyIdent());
                continue;
            }
            Node elt = this.ParseMaybeAssign();
            if (IsDummy(elt))
            {
                if (this.Closes(close, indent, line)) break;
                this.Next();
            }
            else
            {
                elts.Add(elt);
            }
            this.Eat(tt.Comma);
        }
        this.PopCx();
        if (!this.Eat(close))
        {
            // If there is no closing brace, make the node span to the start of the next token.
            this.last.End = this.tok.Start;
            if (this.options.Locations) this.last.Loc!.End = this.tok.Loc!.Start;
        }
        return elts;
    }

    public Node ParseAwait()
    {
        Node node = this.StartNode();
        this.Next();
        node["argument"] = this.ParseMaybeUnary(false);
        return this.FinishNode(node, "AwaitExpression");
    }
}

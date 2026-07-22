using Acorn;
using tt = Acorn.TokenTypes;
using static Acorn.Loose.ParseUtil;

namespace Acorn.Loose;

// Ported from acorn-loose/src/statement.js
public partial class LooseParser
{
    public Node ParseTopLevel()
    {
        Node node = this.StartNodeAt(this.options.Locations
            ? new StoredPos(0, LocUtil.GetLineInfo(this.input, 0))
            : new StoredPos(0, null));
        var body = new List<object?>();
        node["body"] = body;
        while (this.tok.Type != tt.Eof) body.Add(this.ParseStatement());
        this.toks.AdaptDirectivePrologue(body);
        this.last = this.tok;
        node["sourceType"] = this.options.SourceType == "commonjs" ? "script" : this.options.SourceType;
        return this.FinishNode(node, "Program");
    }

    public Node ParseStatement()
    {
        TokenType starttype = this.tok.Type;
        Node node = this.StartNode();
        string? kind = null;

        if (this.toks.IsLet())
        {
            starttype = tt.Var;
            kind = "let";
        }

        if (starttype == tt.Break || starttype == tt.Continue)
        {
            this.Next();
            bool isBreak = starttype == tt.Break;
            if (this.Semicolon() || this.CanInsertSemicolon())
            {
                node["label"] = null;
            }
            else
            {
                node["label"] = this.tok.Type == tt.Name ? this.ParseIdent() : null;
                this.Semicolon();
            }
            return this.FinishNode(node, isBreak ? "BreakStatement" : "ContinueStatement");
        }

        if (starttype == tt.Debugger)
        {
            this.Next();
            this.Semicolon();
            return this.FinishNode(node, "DebuggerStatement");
        }

        if (starttype == tt.Do)
        {
            this.Next();
            node["body"] = this.ParseStatement();
            node["test"] = this.Eat(tt.While) ? this.ParseParenExpression() : this.DummyIdent();
            this.Semicolon();
            return this.FinishNode(node, "DoWhileStatement");
        }

        if (starttype == tt.For)
        {
            this.Next(); // `for` keyword
            bool isAwait = this.options.EcmaVersion >= 9 && this.EatContextual("await");

            this.PushCx();
            this.Expect(tt.ParenL);
            if (this.tok.Type == tt.Semi) return this.ParseFor(node, null);
            bool isLet = this.toks.IsLet();
            bool isAwaitUsing = this.toks.IsAwaitUsing(true);
            bool isUsing = !isAwaitUsing && this.toks.IsUsing(true);

            if (isLet || this.tok.Type == tt.Var || this.tok.Type == tt.Const || isUsing || isAwaitUsing)
            {
                string forKind = isLet ? "let" : isUsing ? "using" : isAwaitUsing ? "await using" : (string)this.tok.Value!;
                Node initDecl = this.StartNode();
                if (isUsing || isAwaitUsing)
                {
                    if (isAwaitUsing) this.Next();
                    this.ParseVar(initDecl, true, forKind);
                }
                else
                {
                    initDecl = this.ParseVar(initDecl, true, forKind);
                }

                if (((List<object?>)initDecl["declarations"]!).Count == 1 && (this.tok.Type == tt.In || this.IsContextual("of")))
                {
                    if (this.options.EcmaVersion >= 9 && this.tok.Type != tt.In)
                        node["await"] = isAwait;
                    return this.ParseForIn(node, initDecl);
                }
                return this.ParseFor(node, initDecl);
            }
            Node initExpr = this.ParseExpression(true);
            if (this.tok.Type == tt.In || this.IsContextual("of"))
            {
                if (this.options.EcmaVersion >= 9 && this.tok.Type != tt.In)
                    node["await"] = isAwait;
                return this.ParseForIn(node, this.ToAssignable(initExpr)!);
            }
            return this.ParseFor(node, initExpr);
        }

        if (starttype == tt.Function)
        {
            this.Next();
            return this.ParseFunction(node, true);
        }

        if (starttype == tt.If)
        {
            this.Next();
            node["test"] = this.ParseParenExpression();
            node["consequent"] = this.ParseStatement();
            node["alternate"] = this.Eat(tt.Else) ? this.ParseStatement() : null;
            return this.FinishNode(node, "IfStatement");
        }

        if (starttype == tt.Return)
        {
            this.Next();
            if (this.Eat(tt.Semi) || this.CanInsertSemicolon()) node["argument"] = null;
            else { node["argument"] = this.ParseExpression(); this.Semicolon(); }
            return this.FinishNode(node, "ReturnStatement");
        }

        if (starttype == tt.Switch)
        {
            int blockIndent = this.curIndent, line = this.curLineStart;
            this.Next();
            node["discriminant"] = this.ParseParenExpression();
            var cases = new List<object?>();
            node["cases"] = cases;
            this.PushCx();
            this.Expect(tt.BraceL);

            Node? cur = null;
            while (!this.Closes(tt.BraceR, blockIndent, line, true))
            {
                if (this.tok.Type == tt.Case || this.tok.Type == tt.Default)
                {
                    bool isCase = this.tok.Type == tt.Case;
                    if (cur != null) this.FinishNode(cur, "SwitchCase");
                    cur = this.StartNode();
                    cases.Add(cur);
                    cur["consequent"] = new List<object?>();
                    this.Next();
                    if (isCase) cur["test"] = this.ParseExpression();
                    else cur["test"] = null;
                    this.Expect(tt.Colon);
                }
                else
                {
                    if (cur == null)
                    {
                        cur = this.StartNode();
                        cases.Add(cur);
                        cur["consequent"] = new List<object?>();
                        cur["test"] = null;
                    }
                    ((List<object?>)cur["consequent"]!).Add(this.ParseStatement());
                }
            }
            if (cur != null) this.FinishNode(cur, "SwitchCase");
            this.PopCx();
            this.Eat(tt.BraceR);
            return this.FinishNode(node, "SwitchStatement");
        }

        if (starttype == tt.Throw)
        {
            this.Next();
            node["argument"] = this.ParseExpression();
            this.Semicolon();
            return this.FinishNode(node, "ThrowStatement");
        }

        if (starttype == tt.Try)
        {
            this.Next();
            node["block"] = this.ParseBlock();
            node["handler"] = null;
            if (this.tok.Type == tt.Catch)
            {
                Node clause = this.StartNode();
                this.Next();
                if (this.Eat(tt.ParenL))
                {
                    clause["param"] = this.ToAssignable(this.ParseExprAtom(), true);
                    this.Expect(tt.ParenR);
                }
                else
                {
                    clause["param"] = null;
                }
                clause["body"] = this.ParseBlock();
                node["handler"] = this.FinishNode(clause, "CatchClause");
            }
            node["finalizer"] = this.Eat(tt.Finally) ? this.ParseBlock() : null;
            if (node["handler"] == null && node["finalizer"] == null) return (Node)node["block"]!;
            return this.FinishNode(node, "TryStatement");
        }

        if (starttype == tt.Var || starttype == tt.Const)
        {
            return this.ParseVar(node, false, kind ?? (string)this.tok.Value!);
        }

        if (starttype == tt.While)
        {
            this.Next();
            node["test"] = this.ParseParenExpression();
            node["body"] = this.ParseStatement();
            return this.FinishNode(node, "WhileStatement");
        }

        if (starttype == tt.With)
        {
            this.Next();
            node["object"] = this.ParseParenExpression();
            node["body"] = this.ParseStatement();
            return this.FinishNode(node, "WithStatement");
        }

        if (starttype == tt.BraceL)
        {
            return this.ParseBlock();
        }

        if (starttype == tt.Semi)
        {
            this.Next();
            return this.FinishNode(node, "EmptyStatement");
        }

        if (starttype == tt.Class)
        {
            return this.ParseClass(true);
        }

        if (starttype == tt.Import)
        {
            if (this.options.EcmaVersion > 10)
            {
                TokenType nextType = this.LookAhead(1).Type;
                if (nextType == tt.ParenL || nextType == tt.Dot)
                {
                    node["expression"] = this.ParseExpression();
                    this.Semicolon();
                    return this.FinishNode(node, "ExpressionStatement");
                }
            }
            return this.ParseImport();
        }

        if (starttype == tt.Export)
        {
            return this.ParseExport();
        }

        // default:
        if (this.toks.IsAsyncFunction())
        {
            this.Next();
            this.Next();
            return this.ParseFunction(node, true, true);
        }

        if (this.toks.IsUsing(false))
        {
            return this.ParseVar(node, false, "using");
        }

        if (this.toks.IsAwaitUsing(false))
        {
            this.Next();
            return this.ParseVar(node, false, "await using");
        }

        Node expr = this.ParseExpression();
        if (IsDummy(expr))
        {
            this.Next();
            if (this.tok.Type == tt.Eof) return this.FinishNode(node, "EmptyStatement");
            return this.ParseStatement();
        }
        else if (starttype == tt.Name && expr.Type == "Identifier" && this.Eat(tt.Colon))
        {
            node["body"] = this.ParseStatement();
            node["label"] = expr;
            return this.FinishNode(node, "LabeledStatement");
        }
        else
        {
            node["expression"] = expr;
            this.Semicolon();
            return this.FinishNode(node, "ExpressionStatement");
        }
    }

    public Node ParseBlock()
    {
        Node node = this.StartNode();
        this.PushCx();
        this.Expect(tt.BraceL);
        int blockIndent = this.curIndent, line = this.curLineStart;
        var body = new List<object?>();
        node["body"] = body;
        while (!this.Closes(tt.BraceR, blockIndent, line, true))
            body.Add(this.ParseStatement());
        this.PopCx();
        this.Eat(tt.BraceR);
        return this.FinishNode(node, "BlockStatement");
    }

    public Node ParseFor(Node node, Node? init)
    {
        node["init"] = init;
        node["test"] = null;
        node["update"] = null;
        if (this.Eat(tt.Semi) && this.tok.Type != tt.Semi) node["test"] = this.ParseExpression();
        if (this.Eat(tt.Semi) && this.tok.Type != tt.ParenR) node["update"] = this.ParseExpression();
        this.PopCx();
        this.Expect(tt.ParenR);
        node["body"] = this.ParseStatement();
        return this.FinishNode(node, "ForStatement");
    }

    public Node ParseForIn(Node node, Node init)
    {
        string type = this.tok.Type == tt.In ? "ForInStatement" : "ForOfStatement";
        this.Next();
        node["left"] = init;
        node["right"] = this.ParseExpression();
        this.PopCx();
        this.Expect(tt.ParenR);
        node["body"] = this.ParseStatement();
        return this.FinishNode(node, type);
    }

    public Node ParseVar(Node node, bool noIn, string kind)
    {
        node["kind"] = kind;
        this.Next();
        var declarations = new List<object?>();
        node["declarations"] = declarations;
        do
        {
            Node decl = this.StartNode();
            decl["id"] = this.options.EcmaVersion >= 6 ? this.ToAssignable(this.ParseExprAtom(), true) : this.ParseIdent();
            decl["init"] = this.Eat(tt.Eq) ? this.ParseMaybeAssign(noIn) : null;
            declarations.Add(this.FinishNode(decl, "VariableDeclarator"));
        } while (this.Eat(tt.Comma));
        if (declarations.Count == 0)
        {
            Node decl = this.StartNode();
            decl["id"] = this.DummyIdent();
            declarations.Add(this.FinishNode(decl, "VariableDeclarator"));
        }
        if (!noIn) this.Semicolon();
        return this.FinishNode(node, "VariableDeclaration");
    }

    public Node ParseClass(object? isStatement)
    {
        Node node = this.StartNode();
        this.Next();
        if (this.tok.Type == tt.Name) node["id"] = this.ParseIdent();
        else if (IsExactlyTrue(isStatement)) node["id"] = this.DummyIdent();
        else node["id"] = null;
        node["superClass"] = this.Eat(tt.Extends) ? this.ParseExpression() : null;
        Node bodyNode = this.StartNode();
        node["body"] = bodyNode;
        var bodyList = new List<object?>();
        bodyNode["body"] = bodyList;
        this.PushCx();
        int indent = this.curIndent + 1, line = this.curLineStart;
        this.Eat(tt.BraceL);
        if (this.curIndent + 1 < indent) { indent = this.curIndent; line = this.curLineStart; }
        while (!this.Closes(tt.BraceR, indent, line))
        {
            Node? element = this.ParseClassElement();
            if (element != null) bodyList.Add(element);
        }
        this.PopCx();
        if (!this.Eat(tt.BraceR))
        {
            // If there is no closing brace, make the node span to the start of the next token.
            this.last.End = this.tok.Start;
            if (this.options.Locations) this.last.Loc!.End = this.tok.Loc!.Start;
        }
        this.Semicolon();
        this.FinishNode(bodyNode, "ClassBody");
        return this.FinishNode(node, IsTruthy(isStatement) ? "ClassDeclaration" : "ClassExpression");
    }

    public Node? ParseClassElement()
    {
        if (this.Eat(tt.Semi)) return null;

        int ecmaVersion = this.options.EcmaVersion;
        bool locations = this.options.Locations;
        int indent = this.curIndent;
        int line = this.curLineStart;
        Node node = this.StartNode();
        string keyName = "";
        bool isGenerator = false;
        bool isAsync = false;
        string kind = "method";
        bool isStatic = false;

        if (this.EatContextual("static"))
        {
            // Parse static init block
            if (ecmaVersion >= 13 && this.Eat(tt.BraceL))
            {
                this.ParseClassStaticBlock(node);
                return node;
            }
            if (this.IsClassElementNameStart() || this.toks.Type == tt.Star)
                isStatic = true;
            else
                keyName = "static";
        }
        node["static"] = isStatic;
        if (keyName == "" && ecmaVersion >= 8 && this.EatContextual("async"))
        {
            if ((this.IsClassElementNameStart() || this.toks.Type == tt.Star) && !this.CanInsertSemicolon())
                isAsync = true;
            else
                keyName = "async";
        }
        if (keyName == "")
        {
            isGenerator = this.Eat(tt.Star);
            object? lastValue = this.toks.Value;
            if (this.EatContextual("get") || this.EatContextual("set"))
            {
                if (this.IsClassElementNameStart())
                    kind = (string)lastValue!;
                else
                    keyName = (string)lastValue!;
            }
        }

        // Parse element name
        if (keyName != "")
        {
            node["computed"] = false;
            Node key = this.StartNodeAt(locations
                ? new StoredPos(this.toks.LastTokStart, this.toks.LastTokStartLoc)
                : new StoredPos(this.toks.LastTokStart, null));
            node["key"] = key;
            key["name"] = keyName;
            this.FinishNode(key, "Identifier");
        }
        else
        {
            this.ParseClassElementName(node);

            // Skip broken stuff.
            if (IsDummy((Node)node["key"]!))
            {
                if (IsDummy(this.ParseMaybeAssign())) this.Next();
                this.Eat(tt.Comma);
                return null;
            }
        }

        // Parse element value
        if (ecmaVersion < 13 || this.toks.Type == tt.ParenL || kind != "method" || isGenerator || isAsync)
        {
            // Method
            Node key = (Node)node["key"]!;
            bool isConstructor =
                !(node["computed"] as bool? ?? false) &&
                !(node["static"] as bool? ?? false) &&
                !isGenerator &&
                !isAsync &&
                kind == "method" && (
                    (key.Type == "Identifier" && (key["name"] as string) == "constructor") ||
                    (key.Type == "Literal" && (key["value"] as string) == "constructor")
                );
            node["kind"] = isConstructor ? "constructor" : kind;
            node["value"] = this.ParseMethod(isGenerator, isAsync);
            this.FinishNode(node, "MethodDefinition");
        }
        else
        {
            // Field
            if (this.Eat(tt.Eq))
            {
                if (this.curLineStart != line && this.curIndent <= indent && this.TokenStartsLine())
                {
                    // Estimated the next line is the next class element by indentations.
                    node["value"] = null;
                }
                else
                {
                    bool oldInAsync = this.inAsync;
                    bool oldInGenerator = this.inGenerator;
                    this.inAsync = false;
                    this.inGenerator = false;
                    node["value"] = this.ParseMaybeAssign();
                    this.inAsync = oldInAsync;
                    this.inGenerator = oldInGenerator;
                }
            }
            else
            {
                node["value"] = null;
            }
            this.Semicolon();
            this.FinishNode(node, "PropertyDefinition");
        }

        return node;
    }

    public Node ParseClassStaticBlock(Node node)
    {
        int blockIndent = this.curIndent, line = this.curLineStart;
        var body = new List<object?>();
        node["body"] = body;
        this.PushCx();
        while (!this.Closes(tt.BraceR, blockIndent, line, true))
            body.Add(this.ParseStatement());
        this.PopCx();
        this.Eat(tt.BraceR);

        return this.FinishNode(node, "StaticBlock");
    }

    public bool IsClassElementNameStart() => this.toks.IsClassElementNameStart();

    public void ParseClassElementName(Node element)
    {
        if (this.toks.Type == tt.PrivateId)
        {
            element["computed"] = false;
            element["key"] = this.ParsePrivateIdent();
        }
        else
        {
            this.ParsePropertyName(element);
        }
    }

    public Node ParseFunction(Node node, object? isStatement, bool isAsync = false)
    {
        bool oldInAsync = this.inAsync, oldInGenerator = this.inGenerator, oldInFunction = this.inFunction;
        this.InitFunction(node);
        if (this.options.EcmaVersion >= 6)
            node["generator"] = this.Eat(tt.Star);
        if (this.options.EcmaVersion >= 8)
            node["async"] = isAsync;
        if (this.tok.Type == tt.Name) node["id"] = this.ParseIdent();
        else if (IsExactlyTrue(isStatement)) node["id"] = this.DummyIdent();
        this.inAsync = node["async"] as bool? ?? false;
        this.inGenerator = node["generator"] as bool? ?? false;
        this.inFunction = true;
        node["params"] = this.ParseFunctionParams();
        node["body"] = this.ParseBlock();
        this.toks.AdaptDirectivePrologue((List<object?>)((Node)node["body"]!)["body"]!);
        this.inAsync = oldInAsync;
        this.inGenerator = oldInGenerator;
        this.inFunction = oldInFunction;
        return this.FinishNode(node, IsTruthy(isStatement) ? "FunctionDeclaration" : "FunctionExpression");
    }

    public Node ParseExport()
    {
        Node node = this.StartNode();
        this.Next();
        if (this.Eat(tt.Star))
        {
            if (this.options.EcmaVersion >= 11)
            {
                if (this.EatContextual("as"))
                    node["exported"] = this.ParseExprAtom();
                else
                    node["exported"] = null;
            }
            node["source"] = this.EatContextual("from") ? this.ParseExprAtom() : this.DummyString();
            if (this.options.EcmaVersion >= 16)
                node["attributes"] = this.ParseWithClause();
            this.Semicolon();
            return this.FinishNode(node, "ExportAllDeclaration");
        }
        if (this.Eat(tt.Default))
        {
            // export default (function foo() {}) // This is FunctionExpression.
            bool isAsync = false;
            if (this.tok.Type == tt.Function || (isAsync = this.toks.IsAsyncFunction()))
            {
                Node fNode = this.StartNode();
                this.Next();
                if (isAsync) this.Next();
                node["declaration"] = this.ParseFunction(fNode, "nullableID", isAsync);
            }
            else if (this.tok.Type == tt.Class)
            {
                node["declaration"] = this.ParseClass("nullableID");
            }
            else
            {
                node["declaration"] = this.ParseMaybeAssign();
                this.Semicolon();
            }
            return this.FinishNode(node, "ExportDefaultDeclaration");
        }
        if (this.tok.Type.Keyword != null || this.toks.IsLet() || this.toks.IsAsyncFunction())
        {
            node["declaration"] = this.ParseStatement();
            node["specifiers"] = new List<object?>();
            node["source"] = null;
        }
        else
        {
            node["declaration"] = null;
            node["specifiers"] = this.ParseExportSpecifierList();
            node["source"] = this.EatContextual("from") ? this.ParseExprAtom() : null;
            if (this.options.EcmaVersion >= 16)
                node["attributes"] = this.ParseWithClause();
            this.Semicolon();
        }
        return this.FinishNode(node, "ExportNamedDeclaration");
    }

    public Node ParseImport()
    {
        Node node = this.StartNode();
        this.Next();
        if (this.tok.Type == tt.String)
        {
            node["specifiers"] = new List<object?>();
            node["source"] = this.ParseExprAtom();
        }
        else
        {
            Node? elt = null;
            if (this.tok.Type == tt.Name && (this.tok.Value as string) != "from")
            {
                elt = this.StartNode();
                elt["local"] = this.ParseIdent();
                this.FinishNode(elt, "ImportDefaultSpecifier");
                this.Eat(tt.Comma);
            }
            var specifiers = this.ParseImportSpecifiers();
            node["specifiers"] = specifiers;
            node["source"] = this.EatContextual("from") && this.tok.Type == tt.String ? this.ParseExprAtom() : this.DummyString();
            if (elt != null) specifiers.Insert(0, elt);
        }
        if (this.options.EcmaVersion >= 16)
            node["attributes"] = this.ParseWithClause();
        this.Semicolon();
        return this.FinishNode(node, "ImportDeclaration");
    }

    public List<object?> ParseImportSpecifiers()
    {
        var elts = new List<object?>();
        if (this.tok.Type == tt.Star)
        {
            Node elt = this.StartNode();
            this.Next();
            elt["local"] = this.EatContextual("as") ? this.ParseIdent() : this.DummyIdent();
            elts.Add(this.FinishNode(elt, "ImportNamespaceSpecifier"));
        }
        else
        {
            int indent = this.curIndent, line = this.curLineStart, continuedLine = this.nextLineStart;
            this.PushCx();
            this.Eat(tt.BraceL);
            if (this.curLineStart > continuedLine) continuedLine = this.curLineStart;
            while (!this.Closes(tt.BraceR, indent + (this.curLineStart <= continuedLine ? 1 : 0), line))
            {
                if (this.IsContextual("from")) break;
                Node elt = this.StartNode();
                if (this.Eat(tt.Star))
                {
                    elt["local"] = this.EatContextual("as") ? this.ParseModuleExportName() : this.DummyIdent();
                    this.FinishNode(elt, "ImportNamespaceSpecifier");
                }
                else
                {
                    elt["imported"] = this.ParseModuleExportName();
                    if (IsDummy((Node)elt["imported"]!)) break;
                    elt["local"] = this.EatContextual("as") ? this.ParseModuleExportName() : elt["imported"];
                    this.FinishNode(elt, "ImportSpecifier");
                }
                elts.Add(elt);
                this.Eat(tt.Comma);
            }
            this.Eat(tt.BraceR);
            this.PopCx();
        }
        return elts;
    }

    public List<object?> ParseWithClause()
    {
        var nodes = new List<object?>();
        if (!this.Eat(tt.With))
        {
            return nodes;
        }

        int indent = this.curIndent, line = this.curLineStart, continuedLine = this.nextLineStart;
        this.PushCx();
        this.Eat(tt.BraceL);
        if (this.curLineStart > continuedLine) continuedLine = this.curLineStart;
        while (!this.Closes(tt.BraceR, indent + (this.curLineStart <= continuedLine ? 1 : 0), line))
        {
            Node attr = this.StartNode();
            attr["key"] = this.tok.Type == tt.String ? this.ParseExprAtom() : this.ParseIdent();
            if (this.Eat(tt.Colon))
            {
                if (this.tok.Type == tt.String)
                    attr["value"] = this.ParseExprAtom();
                else attr["value"] = this.DummyString();
            }
            else
            {
                if (IsDummy((Node)attr["key"]!)) break;
                if (this.tok.Type == tt.String)
                    attr["value"] = this.ParseExprAtom();
                else break;
            }
            nodes.Add(this.FinishNode(attr, "ImportAttribute"));
            this.Eat(tt.Comma);
        }
        this.Eat(tt.BraceR);
        this.PopCx();
        return nodes;
    }

    public List<object?> ParseExportSpecifierList()
    {
        var elts = new List<object?>();
        int indent = this.curIndent, line = this.curLineStart, continuedLine = this.nextLineStart;
        this.PushCx();
        this.Eat(tt.BraceL);
        if (this.curLineStart > continuedLine) continuedLine = this.curLineStart;
        while (!this.Closes(tt.BraceR, indent + (this.curLineStart <= continuedLine ? 1 : 0), line))
        {
            if (this.IsContextual("from")) break;
            Node elt = this.StartNode();
            elt["local"] = this.ParseModuleExportName();
            if (IsDummy((Node)elt["local"]!)) break;
            elt["exported"] = this.EatContextual("as") ? this.ParseModuleExportName() : elt["local"];
            this.FinishNode(elt, "ExportSpecifier");
            elts.Add(elt);
            this.Eat(tt.Comma);
        }
        this.Eat(tt.BraceR);
        this.PopCx();
        return elts;
    }

    public Node ParseModuleExportName()
    {
        return this.options.EcmaVersion >= 13 && this.tok.Type == tt.String
            ? this.ParseExprAtom()
            : this.ParseIdent();
    }
}

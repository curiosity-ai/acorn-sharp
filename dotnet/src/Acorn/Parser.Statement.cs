using System.Numerics;
using tt = Acorn.TokenTypes;
using tc = Acorn.TokContexts;
using static Acorn.ScopeFlags;
using static Acorn.BindFlags;

namespace Acorn;

public partial class Parser
{
    // ### Statement parsing

    // Parse a program. Initializes the parser, reads any number of
    // statements, and wraps them in a Program node.  Optionally takes a
    // `program` argument.  If present, the statements will be appended
    // to its body instead of creating a new node.

    public Node ParseTopLevel(Node node)
    {
        var exports = new Dictionary<string, object?>();
        if (node["body"] == null) node["body"] = new List<object?>();
        var body = (List<object?>)node["body"]!;
        while (Type != tt.Eof)
        {
            Node stmt = ParseStatement(null, true, exports);
            body.Add(stmt);
        }
        if (InModule)
            foreach (var name in new List<string>(UndefinedExports.Keys))
                RaiseRecoverable(UndefinedExports[name].Start, $"Export '{name}' is not defined");
        AdaptDirectivePrologue(body);
        Next();
        node["sourceType"] = Options.SourceType == "commonjs" ? "script" : Options.SourceType;
        return FinishNode(node, "Program");
    }

    private static readonly LabelInfo loopLabel = new LabelInfo { Kind = "loop" };
    private static readonly LabelInfo switchLabel = new LabelInfo { Kind = "switch" };

    public bool IsLet(string? context = null)
    {
        if (Options.EcmaVersion < 6 || !IsContextual("let")) return false;
        var skip = Whitespace.SkipWhiteSpace.Match(Input, Pos);
        int next = Pos + skip.Length;
        int nextCh = FullCharCodeAt(next);
        // For ambiguous cases, determine if a LexicalDeclaration (or only a
        // Statement) is allowed here. If context is not empty then only a Statement
        // is allowed. However, `let [` is an explicit negative lookahead for
        // ExpressionStatement, so special-case it first.
        if (nextCh == 91 || nextCh == 92) return true; // '[', '\'
        if (context != null) return false;

        if (nextCh == 123) return true; // '{'
        if (Identifier.IsIdentifierStart(nextCh))
        {
            int start = next;
            do { next += nextCh <= 0xffff ? 1 : 2; }
            while (Identifier.IsIdentifierChar(nextCh = FullCharCodeAt(next)));
            if (nextCh == 92) return true;
            string ident = Input.Substring(start, next - start);
            if (!Identifier.KeywordRelationalOperator.IsMatch(ident)) return true;
        }
        return false;
    }

    // check 'async [no LineTerminator here] function'
    // - 'async /*foo*/ function' is OK.
    // - 'async /*\n*/ function' is invalid.
    public bool IsAsyncFunction()
    {
        if (Options.EcmaVersion < 8 || !IsContextual("async"))
            return false;

        var skip = Whitespace.SkipWhiteSpace.Match(Input, Pos);
        int next = Pos + skip.Length;
        int after;
        return !Whitespace.LineBreak.IsMatch(Input.Substring(Pos, next - Pos)) &&
            next + 8 <= Input.Length && Input.Substring(next, 8) == "function" &&
            (next + 8 == Input.Length ||
             !(Identifier.IsIdentifierChar(after = FullCharCodeAt(next + 8)) || after == 92 /* '\' */));
    }

    public bool IsUsingKeyword(bool isAwaitUsing, bool isFor)
    {
        if (Options.EcmaVersion < 17 || !IsContextual(isAwaitUsing ? "await" : "using"))
            return false;

        var skip = Whitespace.SkipWhiteSpace.Match(Input, Pos);
        int next = Pos + skip.Length;

        if (Whitespace.LineBreak.IsMatch(Input.Substring(Pos, next - Pos))) return false;

        if (isAwaitUsing)
        {
            int usingEndPos = next + 5 /* using */;
            int after;
            if (usingEndPos > Input.Length || Input.Substring(next, usingEndPos - next) != "using" ||
                usingEndPos == Input.Length ||
                Identifier.IsIdentifierChar(after = FullCharCodeAt(usingEndPos)) ||
                after == 92 /* '\' */
            ) return false;

            var skipAfterUsing = Whitespace.SkipWhiteSpace.Match(Input, usingEndPos);
            next = usingEndPos + skipAfterUsing.Length;
            if (Whitespace.LineBreak.IsMatch(Input.Substring(usingEndPos, next - usingEndPos))) return false;
        }

        int ch = FullCharCodeAt(next);
        if (!Identifier.IsIdentifierStart(ch) && ch != 92 /* '\' */) return false;
        int idStart = next;
        do { next += ch <= 0xffff ? 1 : 2; }
        while (Identifier.IsIdentifierChar(ch = FullCharCodeAt(next)));
        if (ch == 92) return true;
        string id = Input.Substring(idStart, next - idStart);
        if (Identifier.KeywordRelationalOperator.IsMatch(id)) return false;
        if (isFor && !isAwaitUsing && id == "of")
        {
            // Look ahead for using declaration with initializer, i.e., `for (using of = ...)`
            var skipAfterOf = Whitespace.SkipWhiteSpace.Match(Input, next);
            next = next + skipAfterOf.Length;
            if (CharCodeAt(next) != 61 /* '=' */ ||
                // Check for ==, === and => operators
                (ch = CharCodeAt(next + 1)) == 61 /* '=' */ || ch == 62 /* '>' */)
            {
                return false;
            }
        }
        return true;
    }

    public bool IsAwaitUsing(bool isFor)
    {
        return IsUsingKeyword(true, isFor);
    }

    public bool IsUsing(bool isFor)
    {
        return IsUsingKeyword(false, isFor);
    }

    // Parse a single statement.

    public Node ParseStatement(string? context, bool topLevel = false, Dictionary<string, object?>? exports = null)
    {
        TokenType starttype = Type;
        Node node = StartNode();
        string? kind = null;

        if (IsLet(context))
        {
            starttype = tt.Var;
            kind = "let";
        }

        // Most types of statements are recognized by the keyword they
        // start with. Many are trivial to parse, some require a bit of
        // complexity.

        if (starttype == tt.Break || starttype == tt.Continue) return ParseBreakContinueStatement(node, starttype.Keyword!);
        if (starttype == tt.Debugger) return ParseDebuggerStatement(node);
        if (starttype == tt.Do) return ParseDoStatement(node);
        if (starttype == tt.For) return ParseForStatement(node);
        if (starttype == tt.Function)
        {
            // Function as sole body of either an if statement or a labeled statement
            // works, but not when it is part of a labeled statement that is the sole
            // body of an if statement.
            if ((context != null && (Strict || context != "if" && context != "label")) && Options.EcmaVersion >= 6) Unexpected();
            return ParseFunctionStatement(node, false, context == null);
        }
        if (starttype == tt.Class)
        {
            if (context != null) Unexpected();
            return ParseClass(node, true);
        }
        if (starttype == tt.If) return ParseIfStatement(node);
        if (starttype == tt.Return) return ParseReturnStatement(node);
        if (starttype == tt.Switch) return ParseSwitchStatement(node);
        if (starttype == tt.Throw) return ParseThrowStatement(node);
        if (starttype == tt.Try) return ParseTryStatement(node);
        if (starttype == tt.Const || starttype == tt.Var)
        {
            kind = kind ?? (string)Value!;
            if (context != null && kind != "var") Unexpected();
            return ParseVarStatement(node, kind);
        }
        if (starttype == tt.While) return ParseWhileStatement(node);
        if (starttype == tt.With) return ParseWithStatement(node);
        if (starttype == tt.BraceL) return ParseBlock(true, node);
        if (starttype == tt.Semi) return ParseEmptyStatement(node);
        if (starttype == tt.Export || starttype == tt.Import)
        {
            if (Options.EcmaVersion > 10 && starttype == tt.Import)
            {
                var skip = Whitespace.SkipWhiteSpace.Match(Input, Pos);
                int next = Pos + skip.Length;
                int nextCh = CharCodeAt(next);
                if (nextCh == 40 || nextCh == 46) // '(' or '.'
                    return ParseExpressionStatement(node, ParseExpression());
            }

            if (!Options.AllowImportExportEverywhere)
            {
                if (!topLevel)
                    Raise(Start, "'import' and 'export' may only appear at the top level");
                if (!InModule)
                    Raise(Start, "'import' and 'export' may appear only with 'sourceType: module'");
            }
            return starttype == tt.Import ? ParseImport(node) : ParseExport(node, exports);
        }

        // If the statement does not start with a statement keyword or a
        // brace, it's an ExpressionStatement or LabeledStatement. We
        // simply start parsing an expression, and afterwards, if the
        // next token is a colon and the expression was a simple
        // Identifier node, we switch to interpreting it as a label.
        if (IsAsyncFunction())
        {
            if (context != null) Unexpected();
            Next();
            return ParseFunctionStatement(node, true, context == null);
        }

        string? usingKind = IsAwaitUsing(false) ? "await using" : IsUsing(false) ? "using" : null;
        if (usingKind != null)
        {
            if (!AllowUsing)
            {
                Raise(Start, "Using declaration cannot appear in the top level when source type is `script` or in the bare case statement");
            }
            if (context != null)
            {
                // Cases like `for (;;) using x = ...;`, `if (true) await using x = ...;`, etc. are not allowed.
                Raise(Start, "Using declaration is not allowed in single-statement positions");
            }
            if (usingKind == "await using")
            {
                if (!CanAwait)
                {
                    Raise(Start, "Await using cannot appear outside of async function");
                }
                Next();
            }
            Next();
            ParseVar(node, false, usingKind);
            Semicolon();
            return FinishNode(node, "VariableDeclaration");
        }

        object? maybeName = Value;
        Node expr = ParseExpression();
        if (starttype == tt.Name && expr.Type == "Identifier" && Eat(tt.Colon))
            return ParseLabeledStatement(node, (string)maybeName!, expr, context);
        else return ParseExpressionStatement(node, expr);
    }

    public Node ParseBreakContinueStatement(Node node, string keyword)
    {
        bool isBreak = keyword == "break";
        Next();
        if (Eat(tt.Semi) || InsertSemicolon()) node["label"] = null;
        else if (Type != tt.Name) Unexpected();
        else
        {
            node["label"] = ParseIdent();
            Semicolon();
        }

        // Verify that there is an actual destination to break or
        // continue to.
        int i = 0;
        for (; i < Labels.Count; ++i)
        {
            LabelInfo lab = Labels[i];
            if (node["label"] == null || lab.Name == (string?)((Node)node["label"]!)["name"])
            {
                if (lab.Kind != null && (isBreak || lab.Kind == "loop")) break;
                if (node["label"] != null && isBreak) break;
            }
        }
        if (i == Labels.Count) Raise(node.Start, "Unsyntactic " + keyword);
        return FinishNode(node, isBreak ? "BreakStatement" : "ContinueStatement");
    }

    public Node ParseDebuggerStatement(Node node)
    {
        Next();
        Semicolon();
        return FinishNode(node, "DebuggerStatement");
    }

    public Node ParseDoStatement(Node node)
    {
        Next();
        Labels.Add(loopLabel);
        node["body"] = ParseStatement("do");
        Labels.RemoveAt(Labels.Count - 1);
        Expect(tt.While);
        node["test"] = ParseParenExpression();
        if (Options.EcmaVersion >= 6)
            Eat(tt.Semi);
        else
            Semicolon();
        return FinishNode(node, "DoWhileStatement");
    }

    // Disambiguating between a `for` and a `for`/`in` or `for`/`of`
    // loop is non-trivial. See the JS source for details.

    public Node ParseForStatement(Node node)
    {
        Next();
        int awaitAt = (Options.EcmaVersion >= 9 && CanAwait && EatContextual("await")) ? LastTokStart : -1;
        Labels.Add(loopLabel);
        EnterScope(0);
        Expect(tt.ParenL);
        if (Type == tt.Semi)
        {
            if (awaitAt > -1) Unexpected(awaitAt);
            return ParseFor(node, null);
        }
        bool isLet = IsLet();
        if (Type == tt.Var || Type == tt.Const || isLet)
        {
            Node init0 = StartNode();
            string kind = isLet ? "let" : (string)Value!;
            Next();
            ParseVar(init0, true, kind);
            FinishNode(init0, "VariableDeclaration");
            return ParseForAfterInit(node, init0, awaitAt);
        }
        bool startsWithLet = IsContextual("let");
        bool isForOf = false;

        string? usingKind = IsUsing(true) ? "using" : IsAwaitUsing(true) ? "await using" : null;
        if (usingKind != null)
        {
            Node init1 = StartNode();
            Next();
            if (usingKind == "await using")
            {
                if (!CanAwait)
                {
                    Raise(Start, "Await using cannot appear outside of async function");
                }
                Next();
            }
            ParseVar(init1, true, usingKind);
            FinishNode(init1, "VariableDeclaration");
            return ParseForAfterInit(node, init1, awaitAt);
        }
        bool containsEsc = ContainsEsc;
        var refDestructuringErrors = new DestructuringErrors();
        int initPos = Start;
        Node init = awaitAt > -1
            ? ParseExprSubscripts(refDestructuringErrors, "await")
            : ParseExpression(true, refDestructuringErrors);
        if (Type == tt.In || (isForOf = Options.EcmaVersion >= 6 && IsContextual("of")))
        {
            if (awaitAt > -1) // implies `ecmaVersion >= 9` (see declaration of awaitAt)
            {
                if (Type == tt.In) Unexpected(awaitAt);
                node["await"] = true;
            }
            else if (isForOf && Options.EcmaVersion >= 8)
            {
                if (init.Start == initPos && !containsEsc && init.Type == "Identifier" && (string?)init["name"] == "async") Unexpected();
                else if (Options.EcmaVersion >= 9) node["await"] = false;
            }
            if (startsWithLet && isForOf) Raise(init.Start, "The left-hand side of a for-of loop may not start with 'let'.");
            ToAssignable(init, false, refDestructuringErrors);
            CheckLValPattern(init);
            return ParseForIn(node, init);
        }
        else
        {
            CheckExpressionErrors(refDestructuringErrors, true);
        }
        if (awaitAt > -1) Unexpected(awaitAt);
        return ParseFor(node, init);
    }

    // Helper method to parse for loop after variable initialization
    public Node ParseForAfterInit(Node node, Node init, int awaitAt)
    {
        var declarations = (List<object?>)init["declarations"]!;
        if ((Type == tt.In || (Options.EcmaVersion >= 6 && IsContextual("of"))) && declarations.Count == 1)
        {
            if (Type == tt.In)
            {
                string? initKind = (string?)init["kind"];
                if ((initKind == "using" || initKind == "await using") && ((Node)declarations[0]!)["init"] == null)
                {
                    Raise(Start, "Using declaration is not allowed in for-in loops");
                }
                if (Options.EcmaVersion >= 9 && awaitAt > -1) Unexpected(awaitAt);
            }
            else if (Options.EcmaVersion >= 9) node["await"] = awaitAt > -1;
            return ParseForIn(node, init);
        }
        if (awaitAt > -1) Unexpected(awaitAt);
        return ParseFor(node, init);
    }

    public Node ParseFunctionStatement(Node node, bool isAsync, bool declarationPosition)
    {
        Next();
        return ParseFunction(node, FUNC_STATEMENT | (declarationPosition ? 0 : FUNC_HANGING_STATEMENT), false, isAsync);
    }

    public Node ParseIfStatement(Node node)
    {
        Next();
        node["test"] = ParseParenExpression();
        // allow function declarations in branches, but only in non-strict mode
        node["consequent"] = ParseStatement("if");
        node["alternate"] = Eat(tt.Else) ? ParseStatement("if") : null;
        return FinishNode(node, "IfStatement");
    }

    public Node ParseReturnStatement(Node node)
    {
        if (!AllowReturn)
            Raise(Start, "'return' outside of function");
        Next();

        // In `return` (and `break`/`continue`), the keywords with
        // optional arguments, we eagerly look for a semicolon or the
        // possibility to insert one.

        if (Eat(tt.Semi) || InsertSemicolon()) node["argument"] = null;
        else { node["argument"] = ParseExpression(); Semicolon(); }
        return FinishNode(node, "ReturnStatement");
    }

    public Node ParseSwitchStatement(Node node)
    {
        Next();
        node["discriminant"] = ParseParenExpression();
        var cases = new List<object?>();
        node["cases"] = cases;
        Expect(tt.BraceL);
        Labels.Add(switchLabel);
        EnterScope(SCOPE_SWITCH);

        // Statements under must be grouped (by label) in SwitchCase
        // nodes. `cur` is used to keep the node that we are currently
        // adding statements to.

        Node? cur = null;
        for (bool sawDefault = false; Type != tt.BraceR;)
        {
            if (Type == tt.Case || Type == tt.Default)
            {
                bool isCase = Type == tt.Case;
                if (cur != null) FinishNode(cur, "SwitchCase");
                cur = StartNode();
                cases.Add(cur);
                cur["consequent"] = new List<object?>();
                Next();
                if (isCase)
                {
                    cur["test"] = ParseExpression();
                }
                else
                {
                    if (sawDefault) RaiseRecoverable(LastTokStart, "Multiple default clauses");
                    sawDefault = true;
                    cur["test"] = null;
                }
                Expect(tt.Colon);
            }
            else
            {
                if (cur == null) Unexpected();
                ((List<object?>)cur!["consequent"]!).Add(ParseStatement(null));
            }
        }
        ExitScope();
        if (cur != null) FinishNode(cur, "SwitchCase");
        Next(); // Closing brace
        Labels.RemoveAt(Labels.Count - 1);
        return FinishNode(node, "SwitchStatement");
    }

    public Node ParseThrowStatement(Node node)
    {
        Next();
        if (Whitespace.LineBreak.IsMatch(Input.Substring(LastTokEnd, Start - LastTokEnd)))
            Raise(LastTokEnd, "Illegal newline after throw");
        node["argument"] = ParseExpression();
        Semicolon();
        return FinishNode(node, "ThrowStatement");
    }

    public Node ParseCatchClauseParam()
    {
        Node param = ParseBindingAtom();
        bool simple = param.Type == "Identifier";
        EnterScope(simple ? SCOPE_SIMPLE_CATCH : 0);
        CheckLValPattern(param, simple ? BIND_SIMPLE_CATCH : BIND_LEXICAL);
        Expect(tt.ParenR);

        return param;
    }

    public Node ParseTryStatement(Node node)
    {
        Next();
        node["block"] = ParseBlock();
        node["handler"] = null;
        if (Type == tt.Catch)
        {
            Node clause = StartNode();
            Next();
            if (Eat(tt.ParenL))
            {
                clause["param"] = ParseCatchClauseParam();
            }
            else
            {
                if (Options.EcmaVersion < 10) Unexpected();
                clause["param"] = null;
                EnterScope(0);
            }
            clause["body"] = ParseBlock(false);
            ExitScope();
            node["handler"] = FinishNode(clause, "CatchClause");
        }
        node["finalizer"] = Eat(tt.Finally) ? ParseBlock() : null;
        if (node["handler"] == null && node["finalizer"] == null)
            Raise(node.Start, "Missing catch or finally clause");
        return FinishNode(node, "TryStatement");
    }

    public Node ParseVarStatement(Node node, string kind, bool allowMissingInitializer = false)
    {
        Next();
        ParseVar(node, false, kind, allowMissingInitializer);
        Semicolon();
        return FinishNode(node, "VariableDeclaration");
    }

    public Node ParseWhileStatement(Node node)
    {
        Next();
        node["test"] = ParseParenExpression();
        Labels.Add(loopLabel);
        node["body"] = ParseStatement("while");
        Labels.RemoveAt(Labels.Count - 1);
        return FinishNode(node, "WhileStatement");
    }

    public Node ParseWithStatement(Node node)
    {
        if (Strict) Raise(Start, "'with' in strict mode");
        Next();
        node["object"] = ParseParenExpression();
        node["body"] = ParseStatement("with");
        return FinishNode(node, "WithStatement");
    }

    public Node ParseEmptyStatement(Node node)
    {
        Next();
        return FinishNode(node, "EmptyStatement");
    }

    public Node ParseLabeledStatement(Node node, string maybeName, Node expr, string? context)
    {
        foreach (LabelInfo label in Labels)
            if (label.Name == maybeName)
                Raise(expr.Start, "Label '" + maybeName + "' is already declared");
        string? kind = Type.IsLoop ? "loop" : Type == tt.Switch ? "switch" : null;
        for (int i = Labels.Count - 1; i >= 0; i--)
        {
            LabelInfo label = Labels[i];
            if (label.StatementStart == node.Start)
            {
                // Update information about previous labels on this node
                label.StatementStart = Start;
                label.Kind = kind;
            }
            else break;
        }
        Labels.Add(new LabelInfo { Name = maybeName, Kind = kind, StatementStart = Start });
        node["body"] = ParseStatement(context != null ? (context.IndexOf("label") == -1 ? context + "label" : context) : "label");
        Labels.RemoveAt(Labels.Count - 1);
        node["label"] = expr;
        return FinishNode(node, "LabeledStatement");
    }

    public Node ParseExpressionStatement(Node node, Node expr)
    {
        node["expression"] = expr;
        Semicolon();
        return FinishNode(node, "ExpressionStatement");
    }

    // Parse a semicolon-enclosed block of statements, handling `"use
    // strict"` declarations when `allowStrict` is true (used for
    // function bodies).

    public Node ParseBlock(bool createNewLexicalScope = true, Node? node = null, bool? exitStrict = null)
    {
        node ??= StartNode();
        var body = new List<object?>();
        node["body"] = body;
        Expect(tt.BraceL);
        if (createNewLexicalScope) EnterScope(0);
        while (Type != tt.BraceR)
        {
            Node stmt = ParseStatement(null);
            body.Add(stmt);
        }
        if (exitStrict == true) Strict = false;
        Next();
        if (createNewLexicalScope) ExitScope();
        return FinishNode(node, "BlockStatement");
    }

    // Parse a regular `for` loop. The disambiguation code in
    // `parseStatement` will already have parsed the init statement or
    // expression.

    public Node ParseFor(Node node, Node? init)
    {
        node["init"] = init;
        Expect(tt.Semi);
        node["test"] = Type == tt.Semi ? null : ParseExpression();
        Expect(tt.Semi);
        node["update"] = Type == tt.ParenR ? null : ParseExpression();
        Expect(tt.ParenR);
        node["body"] = ParseStatement("for");
        ExitScope();
        Labels.RemoveAt(Labels.Count - 1);
        return FinishNode(node, "ForStatement");
    }

    // Parse a `for`/`in` and `for`/`of` loop, which are almost
    // same from parser's perspective.

    public Node ParseForIn(Node node, Node init)
    {
        bool isForIn = Type == tt.In;
        Next();

        var declarations = (List<object?>)init["declarations"]!;
        if (
            init.Type == "VariableDeclaration" &&
            ((Node)declarations[0]!)["init"] != null &&
            (
                !isForIn ||
                Options.EcmaVersion < 8 ||
                Strict ||
                (string?)init["kind"] != "var" ||
                ((Node)((Node)declarations[0]!)["id"]!).Type != "Identifier"
            )
        )
        {
            Raise(
                init.Start,
                $"{(isForIn ? "for-in" : "for-of")} loop variable declaration may not have an initializer"
            );
        }
        node["left"] = init;
        node["right"] = isForIn ? ParseExpression() : ParseMaybeAssign();
        Expect(tt.ParenR);
        node["body"] = ParseStatement("for");
        ExitScope();
        Labels.RemoveAt(Labels.Count - 1);
        return FinishNode(node, isForIn ? "ForInStatement" : "ForOfStatement");
    }

    // Parse a list of variable declarations.

    public Node ParseVar(Node node, bool isFor, string kind, bool allowMissingInitializer = false)
    {
        var declarations = new List<object?>();
        node["declarations"] = declarations;
        node["kind"] = kind;
        for (; ; )
        {
            Node decl = StartNode();
            ParseVarId(decl, kind);
            if (Eat(tt.Eq))
            {
                decl["init"] = ParseMaybeAssign(isFor);
            }
            else if (!allowMissingInitializer && kind == "const" && !(Type == tt.In || (Options.EcmaVersion >= 6 && IsContextual("of"))))
            {
                Unexpected();
            }
            else if (!allowMissingInitializer && (kind == "using" || kind == "await using") && Options.EcmaVersion >= 17 && Type != tt.In && !IsContextual("of"))
            {
                Raise(LastTokEnd, $"Missing initializer in {kind} declaration");
            }
            else if (!allowMissingInitializer && ((Node)decl["id"]!).Type != "Identifier" && !(isFor && (Type == tt.In || IsContextual("of"))))
            {
                Raise(LastTokEnd, "Complex binding patterns require an initialization value");
            }
            else
            {
                decl["init"] = null;
            }
            declarations.Add(FinishNode(decl, "VariableDeclarator"));
            if (!Eat(tt.Comma)) break;
        }
        return node;
    }

    public void ParseVarId(Node decl, string kind)
    {
        decl["id"] = kind == "using" || kind == "await using"
            ? ParseIdent()
            : ParseBindingAtom();

        CheckLValPattern((Node)decl["id"]!, kind == "var" ? BIND_VAR : BIND_LEXICAL, null);
    }

    private const int FUNC_STATEMENT = 1, FUNC_HANGING_STATEMENT = 2, FUNC_NULLABLE_ID = 4;

    // Parse a function declaration or literal (depending on the
    // `statement & FUNC_STATEMENT`).

    public Node ParseFunction(Node node, int statement, bool allowExpressionBody = false, bool isAsync = false, object? forInit = null)
    {
        InitFunction(node);
        if (Options.EcmaVersion >= 9 || Options.EcmaVersion >= 6 && !isAsync)
        {
            if (Type == tt.Star && (statement & FUNC_HANGING_STATEMENT) != 0)
                Unexpected();
            node["generator"] = Eat(tt.Star);
        }
        if (Options.EcmaVersion >= 8)
            node["async"] = isAsync;

        if ((statement & FUNC_STATEMENT) != 0)
        {
            node["id"] = (statement & FUNC_NULLABLE_ID) != 0 && Type != tt.Name ? null : ParseIdent();
            if (node["id"] != null && (statement & FUNC_HANGING_STATEMENT) == 0)
                // If it is a regular function declaration in sloppy mode, then it is
                // subject to Annex B semantics (BIND_FUNCTION). Otherwise, the binding
                // mode depends on properties of the current scope (see
                // treatFunctionsAsVar).
                CheckLValSimple((Node)node["id"]!, (Strict || Truthy(node["generator"]) || Truthy(node["async"])) ? (TreatFunctionsAsVar ? BIND_VAR : BIND_LEXICAL) : BIND_FUNCTION);
        }

        int oldYieldPos = YieldPos, oldAwaitPos = AwaitPos, oldAwaitIdentPos = AwaitIdentPos;
        YieldPos = 0;
        AwaitPos = 0;
        AwaitIdentPos = 0;
        EnterScope(FunctionFlags(Truthy(node["async"]), Truthy(node["generator"])));

        if ((statement & FUNC_STATEMENT) == 0)
            node["id"] = Type == tt.Name ? ParseIdent() : null;

        ParseFunctionParams(node);
        ParseFunctionBody(node, allowExpressionBody, false, forInit);

        YieldPos = oldYieldPos;
        AwaitPos = oldAwaitPos;
        AwaitIdentPos = oldAwaitIdentPos;
        return FinishNode(node, (statement & FUNC_STATEMENT) != 0 ? "FunctionDeclaration" : "FunctionExpression");
    }

    public void ParseFunctionParams(Node node)
    {
        Expect(tt.ParenL);
        node["params"] = ParseBindingList(tt.ParenR, false, Options.EcmaVersion >= 8);
        CheckYieldAwaitInDefaultParams();
    }

    // Parse a class declaration or literal (depending on the
    // `isStatement` parameter).

    public Node ParseClass(Node node, object? isStatement)
    {
        Next();

        // ecma-262 14.6 Class Definitions
        // A class definition is always strict mode code.
        bool oldStrict = Strict;
        Strict = true;

        ParseClassId(node, isStatement);
        ParseClassSuper(node);
        var privateNameMap = EnterClassBody();
        Node classBody = StartNode();
        bool hadConstructor = false;
        var classBodyBody = new List<object?>();
        classBody["body"] = classBodyBody;
        Expect(tt.BraceL);
        while (Type != tt.BraceR)
        {
            Node? element = ParseClassElement(node["superClass"] != null);
            if (element != null)
            {
                classBodyBody.Add(element);
                if (element.Type == "MethodDefinition" && (string?)element["kind"] == "constructor")
                {
                    if (hadConstructor) RaiseRecoverable(element.Start, "Duplicate constructor in the same class");
                    hadConstructor = true;
                }
                else if (element["key"] != null && ((Node)element["key"]!).Type == "PrivateIdentifier" && IsPrivateNameConflicted(privateNameMap, element))
                {
                    RaiseRecoverable(((Node)element["key"]!).Start, $"Identifier '#{((Node)element["key"]!)["name"]}' has already been declared");
                }
            }
        }
        Strict = oldStrict;
        Next();
        node["body"] = FinishNode(classBody, "ClassBody");
        ExitClassBody();
        return FinishNode(node, Truthy(isStatement) ? "ClassDeclaration" : "ClassExpression");
    }

    public Node? ParseClassElement(bool constructorAllowsSuper)
    {
        if (Eat(tt.Semi)) return null;

        int ecmaVersion = Options.EcmaVersion;
        Node node = StartNode();
        string keyName = "";
        bool isGenerator = false;
        bool isAsync = false;
        string kind = "method";
        bool isStatic = false;

        if (EatContextual("static"))
        {
            // Parse static init block
            if (ecmaVersion >= 13 && Eat(tt.BraceL))
            {
                ParseClassStaticBlock(node);
                return node;
            }
            if (IsClassElementNameStart() || Type == tt.Star)
            {
                isStatic = true;
            }
            else
            {
                keyName = "static";
            }
        }
        node["static"] = isStatic;
        if (keyName.Length == 0 && ecmaVersion >= 8 && EatContextual("async"))
        {
            if ((IsClassElementNameStart() || Type == tt.Star) && !CanInsertSemicolon())
            {
                isAsync = true;
            }
            else
            {
                keyName = "async";
            }
        }
        if (keyName.Length == 0 && (ecmaVersion >= 9 || !isAsync) && Eat(tt.Star))
        {
            isGenerator = true;
        }
        if (keyName.Length == 0 && !isAsync && !isGenerator)
        {
            string lastValue = (string)Value!;
            if (EatContextual("get") || EatContextual("set"))
            {
                if (IsClassElementNameStart())
                {
                    kind = lastValue;
                }
                else
                {
                    keyName = lastValue;
                }
            }
        }

        // Parse element name
        if (keyName.Length != 0)
        {
            // 'async', 'get', 'set', or 'static' were not a keyword contextually.
            // The last token is any of those. Make it the element name.
            node["computed"] = false;
            Node key = StartNodeAt(LastTokStart, LastTokStartLoc);
            node["key"] = key;
            key["name"] = keyName;
            FinishNode(key, "Identifier");
        }
        else
        {
            ParseClassElementName(node);
        }

        // Parse element value
        if (ecmaVersion < 13 || Type == tt.ParenL || kind != "method" || isGenerator || isAsync)
        {
            bool isConstructor = !Truthy(node["static"]) && CheckKeyName(node, "constructor");
            bool allowsDirectSuper = isConstructor && constructorAllowsSuper;
            // Couldn't move this check into the 'parseClassMethod' method for backward compatibility.
            if (isConstructor && kind != "method") Raise(((Node)node["key"]!).Start, "Constructor can't have get/set modifier");
            node["kind"] = isConstructor ? "constructor" : kind;
            ParseClassMethod(node, isGenerator, isAsync, allowsDirectSuper);
        }
        else
        {
            ParseClassField(node);
        }

        return node;
    }

    public bool IsClassElementNameStart()
    {
        return (
            Type == tt.Name ||
            Type == tt.PrivateId ||
            Type == tt.Num ||
            Type == tt.String ||
            Type == tt.BracketL ||
            Type.Keyword != null
        );
    }

    public void ParseClassElementName(Node element)
    {
        if (Type == tt.PrivateId)
        {
            if ((string?)Value == "constructor")
            {
                Raise(Start, "Classes can't have an element named '#constructor'");
            }
            element["computed"] = false;
            element["key"] = ParsePrivateIdent();
        }
        else
        {
            ParsePropertyName(element);
        }
    }

    public Node ParseClassMethod(Node method, bool isGenerator, bool isAsync, bool allowsDirectSuper)
    {
        // Check key and flags
        Node key = (Node)method["key"]!;
        if ((string?)method["kind"] == "constructor")
        {
            if (isGenerator) Raise(key.Start, "Constructor can't be a generator");
            if (isAsync) Raise(key.Start, "Constructor can't be an async method");
        }
        else if (Truthy(method["static"]) && CheckKeyName(method, "prototype"))
        {
            Raise(key.Start, "Classes may not have a static property named prototype");
        }

        // Parse value
        Node value = ParseMethod(isGenerator, isAsync, allowsDirectSuper);
        method["value"] = value;

        // Check value
        var valueParams = (List<object?>)value["params"]!;
        if ((string?)method["kind"] == "get" && valueParams.Count != 0)
            RaiseRecoverable(value.Start, "getter should have no params");
        if ((string?)method["kind"] == "set" && valueParams.Count != 1)
            RaiseRecoverable(value.Start, "setter should have exactly one param");
        if ((string?)method["kind"] == "set" && ((Node)valueParams[0]!).Type == "RestElement")
            RaiseRecoverable(((Node)valueParams[0]!).Start, "Setter cannot use rest params");

        return FinishNode(method, "MethodDefinition");
    }

    public Node ParseClassField(Node field)
    {
        if (CheckKeyName(field, "constructor"))
        {
            Raise(((Node)field["key"]!).Start, "Classes can't have a field named 'constructor'");
        }
        else if (Truthy(field["static"]) && CheckKeyName(field, "prototype"))
        {
            Raise(((Node)field["key"]!).Start, "Classes can't have a static field named 'prototype'");
        }

        if (Eat(tt.Eq))
        {
            // To raise SyntaxError if 'arguments' exists in the initializer.
            EnterScope(SCOPE_CLASS_FIELD_INIT | SCOPE_SUPER);
            field["value"] = ParseMaybeAssign();
            ExitScope();
        }
        else
        {
            field["value"] = null;
        }
        Semicolon();

        return FinishNode(field, "PropertyDefinition");
    }

    public Node ParseClassStaticBlock(Node node)
    {
        var body = new List<object?>();
        node["body"] = body;

        List<LabelInfo> oldLabels = Labels;
        Labels = new List<LabelInfo>();
        EnterScope(SCOPE_CLASS_STATIC_BLOCK | SCOPE_SUPER);
        while (Type != tt.BraceR)
        {
            Node stmt = ParseStatement(null);
            body.Add(stmt);
        }
        Next();
        ExitScope();
        Labels = oldLabels;

        return FinishNode(node, "StaticBlock");
    }

    public void ParseClassId(Node node, object? isStatement)
    {
        if (Type == tt.Name)
        {
            node["id"] = ParseIdent();
            if (Truthy(isStatement))
                CheckLValSimple((Node)node["id"]!, BIND_LEXICAL, null);
        }
        else
        {
            if (isStatement is bool b && b)
                Unexpected();
            node["id"] = null;
        }
    }

    public void ParseClassSuper(Node node)
    {
        node["superClass"] = Eat(tt.Extends) ? ParseExprSubscripts(null, false) : null;
    }

    public Dictionary<string, string?> EnterClassBody()
    {
        var element = new PrivateNameStatus();
        PrivateNameStack.Add(element);
        return element.Declared;
    }

    public void ExitClassBody()
    {
        PrivateNameStatus status = PrivateNameStack[PrivateNameStack.Count - 1];
        PrivateNameStack.RemoveAt(PrivateNameStack.Count - 1);
        var declared = status.Declared;
        var used = status.Used;
        if (!Options.CheckPrivateFields) return;
        int len = PrivateNameStack.Count;
        PrivateNameStatus? parent = len == 0 ? null : PrivateNameStack[len - 1];
        for (int i = 0; i < used.Count; ++i)
        {
            Node id = used[i];
            if (!declared.ContainsKey((string)id["name"]!))
            {
                if (parent != null)
                {
                    parent.Used.Add(id);
                }
                else
                {
                    RaiseRecoverable(id.Start, $"Private field '#{id["name"]}' must be declared in an enclosing class");
                }
            }
        }
    }

    private static bool IsPrivateNameConflicted(Dictionary<string, string?> privateNameMap, Node element)
    {
        string name = (string)((Node)element["key"]!)["name"]!;
        privateNameMap.TryGetValue(name, out var curr);

        string next = "true";
        if (element.Type == "MethodDefinition" && ((string?)element["kind"] == "get" || (string?)element["kind"] == "set"))
        {
            next = (Truthy(element["static"]) ? "s" : "i") + (string)element["kind"]!;
        }

        // `class { get #a(){}; static set #a(_){} }` is also conflict.
        if (
            curr == "iget" && next == "iset" ||
            curr == "iset" && next == "iget" ||
            curr == "sget" && next == "sset" ||
            curr == "sset" && next == "sget"
        )
        {
            privateNameMap[name] = "true";
            return false;
        }
        else if (curr == null)
        {
            privateNameMap[name] = next;
            return false;
        }
        else
        {
            return true;
        }
    }

    private static bool CheckKeyName(Node node, string name)
    {
        bool computed = Truthy(node["computed"]);
        Node key = (Node)node["key"]!;
        return !computed && (
            key.Type == "Identifier" && (string?)key["name"] == name ||
            key.Type == "Literal" && Equals(key["value"], name)
        );
    }

    // Parses module export declaration.

    public Node ParseExportAllDeclaration(Node node, Dictionary<string, object?>? exports)
    {
        if (Options.EcmaVersion >= 11)
        {
            if (EatContextual("as"))
            {
                node["exported"] = ParseModuleExportName();
                CheckExport(exports, node["exported"], LastTokStart);
            }
            else
            {
                node["exported"] = null;
            }
        }
        ExpectContextual("from");
        if (Type != tt.String) Unexpected();
        node["source"] = ParseExprAtom();
        if (Options.EcmaVersion >= 16)
            node["attributes"] = ParseWithClause();
        Semicolon();
        return FinishNode(node, "ExportAllDeclaration");
    }

    public Node ParseExport(Node node, Dictionary<string, object?>? exports)
    {
        Next();
        // export * from '...'
        if (Eat(tt.Star))
        {
            return ParseExportAllDeclaration(node, exports);
        }
        if (Eat(tt.Default))
        { // export default ...
            CheckExport(exports, "default", LastTokStart);
            node["declaration"] = ParseExportDefaultDeclaration();
            return FinishNode(node, "ExportDefaultDeclaration");
        }
        // export var|const|let|function|class ...
        if (ShouldParseExportStatement())
        {
            node["declaration"] = ParseExportDeclaration(node);
            Node declaration = (Node)node["declaration"]!;
            if (declaration.Type == "VariableDeclaration")
                CheckVariableExport(exports, (List<object?>)declaration["declarations"]!);
            else
                CheckExport(exports, declaration["id"], ((Node)declaration["id"]!).Start);
            node["specifiers"] = new List<object?>();
            node["source"] = null;
            if (Options.EcmaVersion >= 16)
                node["attributes"] = new List<object?>();
        }
        else
        { // export { x, y as z } [from '...']
            node["declaration"] = null;
            node["specifiers"] = ParseExportSpecifiers(exports);
            if (EatContextual("from"))
            {
                if (Type != tt.String) Unexpected();
                node["source"] = ParseExprAtom();
                if (Options.EcmaVersion >= 16)
                    node["attributes"] = ParseWithClause();
            }
            else
            {
                foreach (var specObj in (List<object?>)node["specifiers"]!)
                {
                    Node spec = (Node)specObj!;
                    // check for keywords used as local names
                    CheckUnreserved((Node)spec["local"]!);
                    // check if export is defined
                    CheckLocalExport((Node)spec["local"]!);

                    if (((Node)spec["local"]!).Type == "Literal")
                    {
                        Raise(((Node)spec["local"]!).Start, "A string literal cannot be used as an exported binding without `from`.");
                    }
                }

                node["source"] = null;
                if (Options.EcmaVersion >= 16)
                    node["attributes"] = new List<object?>();
            }
            Semicolon();
        }
        return FinishNode(node, "ExportNamedDeclaration");
    }

    public Node ParseExportDeclaration(Node node)
    {
        return ParseStatement(null);
    }

    public Node ParseExportDefaultDeclaration()
    {
        bool isAsync;
        if (Type == tt.Function || (isAsync = IsAsyncFunction()))
        {
            Node fNode = StartNode();
            Next();
            if (isAsync) Next();
            return ParseFunction(fNode, FUNC_STATEMENT | FUNC_NULLABLE_ID, false, isAsync);
        }
        else if (Type == tt.Class)
        {
            Node cNode = StartNode();
            return ParseClass(cNode, "nullableID");
        }
        else
        {
            Node declaration = ParseMaybeAssign();
            Semicolon();
            return declaration;
        }
    }

    public void CheckExport(Dictionary<string, object?>? exports, object? name, int pos)
    {
        if (exports == null) return;
        string nameStr;
        if (name is string s)
            nameStr = s;
        else
        {
            Node n = (Node)name!;
            nameStr = n.Type == "Identifier" ? (string)n["name"]! : (string)n["value"]!;
        }
        if (exports.ContainsKey(nameStr))
            RaiseRecoverable(pos, "Duplicate export '" + nameStr + "'");
        exports[nameStr] = true;
    }

    public void CheckPatternExport(Dictionary<string, object?>? exports, Node pat)
    {
        string type = pat.Type;
        if (type == "Identifier")
            CheckExport(exports, pat, pat.Start);
        else if (type == "ObjectPattern")
            foreach (var prop in (List<object?>)pat["properties"]!)
                CheckPatternExport(exports, (Node)prop!);
        else if (type == "ArrayPattern")
            foreach (var elt in (List<object?>)pat["elements"]!)
            {
                if (elt != null) CheckPatternExport(exports, (Node)elt);
            }
        else if (type == "Property")
            CheckPatternExport(exports, (Node)pat["value"]!);
        else if (type == "AssignmentPattern")
            CheckPatternExport(exports, (Node)pat["left"]!);
        else if (type == "RestElement")
            CheckPatternExport(exports, (Node)pat["argument"]!);
    }

    public void CheckVariableExport(Dictionary<string, object?>? exports, List<object?> decls)
    {
        if (exports == null) return;
        foreach (var declObj in decls)
            CheckPatternExport(exports, (Node)((Node)declObj!)["id"]!);
    }

    public bool ShouldParseExportStatement()
    {
        return Type.Keyword == "var" ||
            Type.Keyword == "const" ||
            Type.Keyword == "class" ||
            Type.Keyword == "function" ||
            IsLet() ||
            IsAsyncFunction();
    }

    // Parses a comma-separated list of module exports.

    public Node ParseExportSpecifier(Dictionary<string, object?>? exports)
    {
        Node node = StartNode();
        node["local"] = ParseModuleExportName();

        node["exported"] = EatContextual("as") ? ParseModuleExportName() : node["local"];
        CheckExport(
            exports,
            node["exported"],
            ((Node)node["exported"]!).Start
        );

        return FinishNode(node, "ExportSpecifier");
    }

    public List<object?> ParseExportSpecifiers(Dictionary<string, object?>? exports)
    {
        var nodes = new List<object?>();
        bool first = true;
        // export { x, y as z } [from '...']
        Expect(tt.BraceL);
        while (!Eat(tt.BraceR))
        {
            if (!first)
            {
                Expect(tt.Comma);
                if (AfterTrailingComma(tt.BraceR)) break;
            }
            else first = false;

            nodes.Add(ParseExportSpecifier(exports));
        }
        return nodes;
    }

    // Parses import declaration.

    public Node ParseImport(Node node)
    {
        Next();

        // import '...'
        if (Type == tt.String)
        {
            node["specifiers"] = new List<object?>();
            node["source"] = ParseExprAtom();
        }
        else
        {
            node["specifiers"] = ParseImportSpecifiers();
            ExpectContextual("from");
            if (Type == tt.String) node["source"] = ParseExprAtom();
            else { Unexpected(); node["source"] = null; }
        }
        if (Options.EcmaVersion >= 16)
            node["attributes"] = ParseWithClause();
        Semicolon();
        return FinishNode(node, "ImportDeclaration");
    }

    // Parses a comma-separated list of module imports.

    public Node ParseImportSpecifier()
    {
        Node node = StartNode();
        node["imported"] = ParseModuleExportName();

        if (EatContextual("as"))
        {
            node["local"] = ParseIdent();
        }
        else
        {
            CheckUnreserved((Node)node["imported"]!);
            node["local"] = node["imported"];
        }
        CheckLValSimple((Node)node["local"]!, BIND_LEXICAL);

        return FinishNode(node, "ImportSpecifier");
    }

    public Node ParseImportDefaultSpecifier()
    {
        // import defaultObj, { x, y as z } from '...'
        Node node = StartNode();
        node["local"] = ParseIdent();
        CheckLValSimple((Node)node["local"]!, BIND_LEXICAL);
        return FinishNode(node, "ImportDefaultSpecifier");
    }

    public Node ParseImportNamespaceSpecifier()
    {
        Node node = StartNode();
        Next();
        ExpectContextual("as");
        node["local"] = ParseIdent();
        CheckLValSimple((Node)node["local"]!, BIND_LEXICAL);
        return FinishNode(node, "ImportNamespaceSpecifier");
    }

    public List<object?> ParseImportSpecifiers()
    {
        var nodes = new List<object?>();
        bool first = true;
        if (Type == tt.Name)
        {
            nodes.Add(ParseImportDefaultSpecifier());
            if (!Eat(tt.Comma)) return nodes;
        }
        if (Type == tt.Star)
        {
            nodes.Add(ParseImportNamespaceSpecifier());
            return nodes;
        }
        Expect(tt.BraceL);
        while (!Eat(tt.BraceR))
        {
            if (!first)
            {
                Expect(tt.Comma);
                if (AfterTrailingComma(tt.BraceR)) break;
            }
            else first = false;

            nodes.Add(ParseImportSpecifier());
        }
        return nodes;
    }

    public List<object?> ParseWithClause()
    {
        var nodes = new List<object?>();
        if (!Eat(tt.With))
        {
            return nodes;
        }
        Expect(tt.BraceL);
        var attributeKeys = new Dictionary<string, object?>();
        bool first = true;
        while (!Eat(tt.BraceR))
        {
            if (!first)
            {
                Expect(tt.Comma);
                if (AfterTrailingComma(tt.BraceR)) break;
            }
            else first = false;

            Node attr = ParseImportAttribute();
            Node attrKey = (Node)attr["key"]!;
            string keyName = attrKey.Type == "Identifier" ? (string)attrKey["name"]! : (string)attrKey["value"]!;
            if (attributeKeys.ContainsKey(keyName))
                RaiseRecoverable(attrKey.Start, "Duplicate attribute key '" + keyName + "'");
            attributeKeys[keyName] = true;
            nodes.Add(attr);
        }
        return nodes;
    }

    public Node ParseImportAttribute()
    {
        Node node = StartNode();
        node["key"] = Type == tt.String ? ParseExprAtom() : ParseIdent(Options.AllowReserved != AllowReservedOption.Never);
        Expect(tt.Colon);
        if (Type != tt.String)
        {
            Unexpected();
        }
        node["value"] = ParseExprAtom();
        return FinishNode(node, "ImportAttribute");
    }

    public Node ParseModuleExportName()
    {
        if (Options.EcmaVersion >= 13 && Type == tt.String)
        {
            Node stringLiteral = ParseLiteral(Value);
            if (Util.LoneSurrogate.IsMatch((string)stringLiteral["value"]!))
            {
                Raise(stringLiteral.Start, "An export name cannot include a lone surrogate.");
            }
            return stringLiteral;
        }
        return ParseIdent(true);
    }

    // Set `ExpressionStatement#directive` property for directive prologues.
    public void AdaptDirectivePrologue(List<object?> statements)
    {
        for (int i = 0; i < statements.Count && IsDirectiveCandidate((Node)statements[i]!); ++i)
        {
            string raw = (string)((Node)((Node)statements[i]!)["expression"]!)["raw"]!;
            ((Node)statements[i]!)["directive"] = raw.Substring(1, raw.Length - 2);
        }
    }

    public bool IsDirectiveCandidate(Node statement)
    {
        return (
            Options.EcmaVersion >= 5 &&
            statement.Type == "ExpressionStatement" &&
            ((Node)statement["expression"]!).Type == "Literal" &&
            ((Node)statement["expression"]!)["value"] is string &&
            // Reject parenthesized strings.
            (Input[statement.Start] == '"' || Input[statement.Start] == '\'')
        );
    }
}

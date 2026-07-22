using System.Numerics;
using tt = Acorn.TokenTypes;
using tc = Acorn.TokContexts;
using static Acorn.ScopeFlags;
using static Acorn.BindFlags;

namespace Acorn;

public partial class Parser
{
    // Convert existing expression atom to assignable pattern
    // if possible.

    public Node? ToAssignable(Node? node, bool isBinding = false, DestructuringErrors? refDestructuringErrors = null)
    {
        if (Options.EcmaVersion >= 6 && node != null)
        {
            switch (node.Type)
            {
                case "Identifier":
                    if (InAsync && (string?)node["name"] == "await")
                        Raise(node.Start, "Cannot use 'await' as identifier inside an async function");
                    break;

                case "ObjectPattern":
                case "ArrayPattern":
                case "AssignmentPattern":
                case "RestElement":
                    break;

                case "ObjectExpression":
                    node.Type = "ObjectPattern";
                    if (refDestructuringErrors != null) CheckPatternErrors(refDestructuringErrors, true);
                    foreach (var propObj in (List<object?>)node["properties"]!)
                    {
                        var prop = (Node)propObj!;
                        ToAssignable(prop, isBinding);
                        // Early error:
                        //   AssignmentRestProperty[Yield, Await] :
                        //     `...` DestructuringAssignmentTarget[Yield, Await]
                        //
                        //   It is a Syntax Error if |DestructuringAssignmentTarget| is an |ArrayLiteral| or an |ObjectLiteral|.
                        if (prop.Type == "RestElement")
                        {
                            var arg = (Node)prop["argument"]!;
                            if (arg.Type == "ArrayPattern" || arg.Type == "ObjectPattern")
                                Raise(arg.Start, "Unexpected token");
                        }
                    }
                    break;

                case "Property":
                    // AssignmentProperty has type === "Property"
                    if ((string?)node["kind"] != "init") Raise(((Node)node["key"]!).Start, "Object pattern can't contain getter or setter");
                    ToAssignable((Node)node["value"]!, isBinding);
                    break;

                case "ArrayExpression":
                    node.Type = "ArrayPattern";
                    if (refDestructuringErrors != null) CheckPatternErrors(refDestructuringErrors, true);
                    ToAssignableList((List<object?>)node["elements"]!, isBinding);
                    break;

                case "SpreadElement":
                    node.Type = "RestElement";
                    ToAssignable((Node)node["argument"]!, isBinding);
                    if (((Node)node["argument"]!).Type == "AssignmentPattern")
                        Raise(((Node)node["argument"]!).Start, "Rest elements cannot have a default value");
                    break;

                case "AssignmentExpression":
                    if ((string?)node["operator"] != "=") Raise(((Node)node["left"]!).End, "Only '=' operator can be used for specifying default value.");
                    node.Type = "AssignmentPattern";
                    node.Remove("operator");
                    ToAssignable((Node)node["left"]!, isBinding);
                    break;

                case "ParenthesizedExpression":
                    ToAssignable((Node)node["expression"]!, isBinding, refDestructuringErrors);
                    break;

                case "ChainExpression":
                    RaiseRecoverable(node.Start, "Optional chaining cannot appear in left-hand side");
                    break;

                case "MemberExpression":
                    if (!isBinding) break;
                    goto default;

                default:
                    Raise(node.Start, "Assigning to rvalue");
                    break;
            }
        }
        else if (refDestructuringErrors != null) CheckPatternErrors(refDestructuringErrors, true);
        return node;
    }

    // Convert list of expression atoms to binding list.

    public List<object?> ToAssignableList(List<object?> exprList, bool isBinding)
    {
        int end = exprList.Count;
        for (int i = 0; i < end; i++)
        {
            var elt = (Node?)exprList[i];
            if (elt != null) ToAssignable(elt, isBinding);
        }
        if (end != 0)
        {
            var last = (Node?)exprList[end - 1];
            if (Options.EcmaVersion == 6 && isBinding && last != null && last.Type == "RestElement" && ((Node)last["argument"]!).Type != "Identifier")
                Unexpected(((Node)last["argument"]!).Start);
        }
        return exprList;
    }

    // Parses spread element.

    public Node ParseSpread(DestructuringErrors? refDestructuringErrors)
    {
        Node node = StartNode();
        Next();
        node["argument"] = ParseMaybeAssign(false, refDestructuringErrors);
        return FinishNode(node, "SpreadElement");
    }

    public Node ParseRestBinding()
    {
        Node node = StartNode();
        Next();

        // RestElement inside of a function parameter must be an identifier
        if (Options.EcmaVersion == 6 && Type != tt.Name)
            Unexpected();

        node["argument"] = ParseBindingAtom();

        return FinishNode(node, "RestElement");
    }

    // Parses lvalue (assignable) atom.

    public Node ParseBindingAtom()
    {
        if (Options.EcmaVersion >= 6)
        {
            if (Type == tt.BracketL)
            {
                Node node = StartNode();
                Next();
                node["elements"] = ParseBindingList(tt.BracketR, true, true);
                return FinishNode(node, "ArrayPattern");
            }
            if (Type == tt.BraceL)
            {
                return ParseObj(true);
            }
        }
        return ParseIdent();
    }

    public List<object?> ParseBindingList(TokenType close, bool allowEmpty, bool allowTrailingComma, bool allowModifiers = false)
    {
        var elts = new List<object?>();
        bool first = true;
        while (!Eat(close))
        {
            if (first) first = false;
            else Expect(tt.Comma);
            if (allowEmpty && Type == tt.Comma)
            {
                elts.Add(null);
            }
            else if (allowTrailingComma && AfterTrailingComma(close))
            {
                break;
            }
            else if (Type == tt.Ellipsis)
            {
                Node rest = ParseRestBinding();
                ParseBindingListItem(rest);
                elts.Add(rest);
                if (Type == tt.Comma) RaiseRecoverable(Start, "Comma is not permitted after the rest element");
                Expect(close);
                break;
            }
            else
            {
                elts.Add(ParseAssignableListItem(allowModifiers));
            }
        }
        return elts;
    }

    public Node ParseAssignableListItem(bool allowModifiers)
    {
        Node elem = ParseMaybeDefault(Start, StartLoc);
        ParseBindingListItem(elem);
        return elem;
    }

    public Node ParseBindingListItem(Node param)
    {
        return param;
    }

    // Parses assignment pattern around given atom if possible.

    public Node ParseMaybeDefault(int startPos, Position? startLoc, Node? left = null)
    {
        left ??= ParseBindingAtom();
        if (Options.EcmaVersion < 6 || !Eat(tt.Eq)) return left;
        Node node = StartNodeAt(startPos, startLoc);
        node["left"] = left;
        node["right"] = ParseMaybeAssign();
        return FinishNode(node, "AssignmentPattern");
    }

    // The following three functions all verify that a node is an lvalue —
    // something that can be bound, or assigned to. See the JS source for a full
    // description of the three variants.

    public void CheckLValSimple(Node expr, int bindingType = BIND_NONE, Dictionary<string, object?>? checkClashes = null)
    {
        bool isBind = bindingType != BIND_NONE;

        switch (expr.Type)
        {
            case "Identifier":
                if (Strict && ReservedWordsStrictBind.IsMatch((string)expr["name"]!))
                    RaiseRecoverable(expr.Start, (isBind ? "Binding " : "Assigning to ") + (string)expr["name"]! + " in strict mode");
                if (isBind)
                {
                    if (bindingType == BIND_LEXICAL && (string?)expr["name"] == "let")
                        RaiseRecoverable(expr.Start, "let is disallowed as a lexically bound name");
                    if (checkClashes != null)
                    {
                        if (checkClashes.ContainsKey((string)expr["name"]!))
                            RaiseRecoverable(expr.Start, "Argument name clash");
                        checkClashes[(string)expr["name"]!] = true;
                    }
                    if (bindingType != BIND_OUTSIDE) DeclareName((string)expr["name"]!, bindingType, expr.Start);
                }
                break;

            case "ChainExpression":
                RaiseRecoverable(expr.Start, "Optional chaining cannot appear in left-hand side");
                break;

            case "MemberExpression":
                if (isBind) RaiseRecoverable(expr.Start, "Binding member expression");
                break;

            case "ParenthesizedExpression":
                if (isBind) RaiseRecoverable(expr.Start, "Binding parenthesized expression");
                CheckLValSimple((Node)expr["expression"]!, bindingType, checkClashes);
                return;

            default:
                Raise(expr.Start, (isBind ? "Binding" : "Assigning to") + " rvalue");
                break;
        }
    }

    public void CheckLValPattern(Node expr, int bindingType = BIND_NONE, Dictionary<string, object?>? checkClashes = null)
    {
        switch (expr.Type)
        {
            case "ObjectPattern":
                foreach (var propObj in (List<object?>)expr["properties"]!)
                {
                    CheckLValInnerPattern((Node)propObj!, bindingType, checkClashes);
                }
                break;

            case "ArrayPattern":
                foreach (var elemObj in (List<object?>)expr["elements"]!)
                {
                    if (elemObj != null) CheckLValInnerPattern((Node)elemObj, bindingType, checkClashes);
                }
                break;

            default:
                CheckLValSimple(expr, bindingType, checkClashes);
                break;
        }
    }

    public void CheckLValInnerPattern(Node expr, int bindingType = BIND_NONE, Dictionary<string, object?>? checkClashes = null)
    {
        switch (expr.Type)
        {
            case "Property":
                // AssignmentProperty has type === "Property"
                CheckLValInnerPattern((Node)expr["value"]!, bindingType, checkClashes);
                break;

            case "AssignmentPattern":
                CheckLValPattern((Node)expr["left"]!, bindingType, checkClashes);
                break;

            case "RestElement":
                CheckLValPattern((Node)expr["argument"]!, bindingType, checkClashes);
                break;

            default:
                CheckLValPattern(expr, bindingType, checkClashes);
                break;
        }
    }
}

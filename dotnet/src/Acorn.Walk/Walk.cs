// AST walker module for ESTree compatible trees.
//
// Faithful C# port of acorn-walk (acorn-walk/src/index.js). The JS module is a
// set of closures that thread a recursive "continue" callback (named `c`
// throughout the original) through a table of per-node-type visitor functions.
// In this port the recursive callback is modelled by <see cref="Walker"/>: base
// visitors receive a <see cref="Walker"/> and call <c>c.Recurse(child, st,
// "Type")</c> to descend, exactly mirroring the JS <c>c(child, st, "Type")</c>.

using Acorn;

namespace Acorn.Walk;

/// <summary>
/// A base/recursive visitor function for a node type. Mirrors the JS
/// <c>(node, st, c) =&gt; ...</c> functions stored on the <c>base</c> table.
/// <paramref name="c"/> is the recurse callback (JS <c>c</c>).
/// </summary>
public delegate void WalkFn(Node node, object? state, Walker c);

/// <summary>Predicate for the <c>findNode*</c> helpers: <c>(type, node) =&gt; bool</c>.</summary>
public delegate bool FindPredicate(string type, Node node);

/// <summary>Result of a successful <c>findNode*</c> search (JS <c>Found</c>).</summary>
public sealed class Found
{
    public Node Node;
    public object? State;

    public Found(Node node, object? state)
    {
        Node = node;
        State = state;
    }
}

/// <summary>
/// The recurse callback threaded through a walk (JS's <c>c</c>). Each public
/// walk entry point builds a <see cref="Walker"/> wrapping a closure that
/// implements that walk's specific step; base visitors invoke it via
/// <see cref="Recurse"/> to descend into children.
/// </summary>
public sealed class Walker
{
    private readonly Action<Node, object?, string?> _recurse;

    internal Walker(Action<Node, object?, string?> recurse) => _recurse = recurse;

    /// <summary>
    /// Continue the walk on <paramref name="node"/>. <paramref name="override"/>
    /// forces the visitor-table entry used (JS's third <c>override</c> argument),
    /// e.g. <c>"Expression"</c>, <c>"Pattern"</c>; when null the node's own
    /// <see cref="Node.Type"/> is used.
    /// </summary>
    public void Recurse(Node node, object? state, string? @override = null) => _recurse(node, state, @override);
}

public static class AstWalker
{
    // Carries a Found through the early-exit throw the JS code performs.
    private sealed class FoundException : Exception
    {
        public readonly Found Found;
        public FoundException(Found found) => Found = found;
    }

    private static void VisitNode(IReadOnlyDictionary<string, WalkFn> baseVisitor, string type, Node node, object? st, Walker c)
    {
        if (!baseVisitor.TryGetValue(type, out var fn) || fn == null)
            throw new InvalidOperationException($"No walker function defined for node type {type}");
        fn(node, st, c);
    }

    private static FindPredicate MakeTest(object? test)
    {
        if (test is string s)
            return (type, _) => type == s;
        if (test == null)
            return (_, _) => true;
        if (test is FindPredicate p)
            return p;
        if (test is Func<string, Node, bool> f)
            return (type, node) => f(type, node);
        throw new ArgumentException("test must be a string, FindPredicate, or Func<string, Node, bool>", nameof(test));
    }

    // A simple walk is one where you simply specify callbacks to be called on
    // specific nodes. Visitors are invoked "on exit" after the base walker has
    // descended into the node's children.
    public static void Simple(Node node, IReadOnlyDictionary<string, Action<Node, object?>> visitors,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null, string? @override = null)
    {
        baseVisitor ??= BaseVisitor;
        Walker c = null!;
        c = new Walker((n, st, ov) =>
        {
            string type = ov ?? n.Type;
            VisitNode(baseVisitor, type, n, st, c);
            if (visitors.TryGetValue(type, out var v))
                v(n, st);
        });
        c.Recurse(node, state, @override);
    }

    // An ancestor walk keeps an array of ancestor nodes (including the current
    // node) and passes them to the callback as third parameter (and also as
    // state parameter when no other state is present).
    public static void Ancestor(Node node, IReadOnlyDictionary<string, Action<Node, object?, List<Node>>> visitors,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null, string? @override = null)
    {
        var ancestors = new List<Node>();
        baseVisitor ??= BaseVisitor;
        Walker c = null!;
        c = new Walker((n, st, ov) =>
        {
            string type = ov ?? n.Type;
            bool isNew = ancestors.Count == 0 || !ReferenceEquals(n, ancestors[^1]);
            if (isNew) ancestors.Add(n);
            VisitNode(baseVisitor, type, n, st, c);
            if (visitors.TryGetValue(type, out var v))
                v(n, st ?? ancestors, ancestors);
            if (isNew) ancestors.RemoveAt(ancestors.Count - 1);
        });
        c.Recurse(node, state, @override);
    }

    // A recursive walk is one where your functions override the default walkers.
    // They can modify and replace the threaded state, and decide how and whether
    // to walk their child nodes (by calling their third argument).
    public static void Recursive(Node node, object? state, IReadOnlyDictionary<string, WalkFn>? funcs,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, string? @override = null)
    {
        IReadOnlyDictionary<string, WalkFn> visitor = funcs != null ? Make(funcs, baseVisitor) : baseVisitor!;
        Walker c = null!;
        c = new Walker((n, st, ov) =>
        {
            visitor[ov ?? n.Type](n, st, c);
        });
        c.Recurse(node, state, @override);
    }

    // A full walk triggers the callback (node, state, type) on each node.
    public static void Full(Node node, Action<Node, object?, string> callback,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null, string? @override = null)
    {
        baseVisitor ??= BaseVisitor;
        Node? last = null;
        Walker c = null!;
        c = new Walker((n, st, ov) =>
        {
            string type = ov ?? n.Type;
            VisitNode(baseVisitor, type, n, st, c);
            if (!ReferenceEquals(last, n))
            {
                callback(n, st, type);
                last = n;
            }
        });
        c.Recurse(node, state, @override);
    }

    // A fullAncestor walk is like an ancestor walk, but triggers the callback on
    // each node.
    public static void FullAncestor(Node node, Action<Node, object?, List<Node>, string> callback,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null)
    {
        baseVisitor ??= BaseVisitor;
        var ancestors = new List<Node>();
        Node? last = null;
        Walker c = null!;
        c = new Walker((n, st, ov) =>
        {
            string type = ov ?? n.Type;
            bool isNew = ancestors.Count == 0 || !ReferenceEquals(n, ancestors[^1]);
            if (isNew) ancestors.Add(n);
            VisitNode(baseVisitor, type, n, st, c);
            if (!ReferenceEquals(last, n))
            {
                callback(n, st ?? ancestors, ancestors, type);
                last = n;
            }
            if (isNew) ancestors.RemoveAt(ancestors.Count - 1);
        });
        c.Recurse(node, state);
    }

    // Find a node with a given start, end, and type (all optional; null is a
    // wildcard). Returns a Found, or null when no matching node exists. Nodes are
    // tested from inner to outer, so the innermost match wins.
    public static Found? FindNodeAt(Node node, int? start, int? end = null, object? test = null,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null)
    {
        baseVisitor ??= BaseVisitor;
        var pred = MakeTest(test);
        try
        {
            Walker c = null!;
            c = new Walker((n, st, ov) =>
            {
                string type = ov ?? n.Type;
                if ((start == null || n.Start <= start) &&
                    (end == null || n.End >= end))
                    VisitNode(baseVisitor, type, n, st, c);
                if ((start == null || n.Start == start) &&
                    (end == null || n.End == end) &&
                    pred(type, n))
                    throw new FoundException(new Found(n, st));
            });
            c.Recurse(node, state);
        }
        catch (FoundException e)
        {
            return e.Found;
        }
        return null;
    }

    // Find the innermost node of a given type that contains the given position.
    public static Found? FindNodeAround(Node node, int pos, object? test = null,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null)
    {
        var pred = MakeTest(test);
        baseVisitor ??= BaseVisitor;
        try
        {
            Walker c = null!;
            c = new Walker((n, st, ov) =>
            {
                string type = ov ?? n.Type;
                if (n.Start > pos || n.End < pos) return;
                VisitNode(baseVisitor, type, n, st, c);
                if (pred(type, n)) throw new FoundException(new Found(n, st));
            });
            c.Recurse(node, state);
        }
        catch (FoundException e)
        {
            return e.Found;
        }
        return null;
    }

    // Find the outermost matching node after a given position.
    public static Found? FindNodeAfter(Node node, int pos, object? test = null,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null)
    {
        var pred = MakeTest(test);
        baseVisitor ??= BaseVisitor;
        try
        {
            Walker c = null!;
            c = new Walker((n, st, ov) =>
            {
                if (n.End < pos) return;
                string type = ov ?? n.Type;
                if (n.Start >= pos && pred(type, n)) throw new FoundException(new Found(n, st));
                VisitNode(baseVisitor, type, n, st, c);
            });
            c.Recurse(node, state);
        }
        catch (FoundException e)
        {
            return e.Found;
        }
        return null;
    }

    // Find the outermost matching node before a given position.
    public static Found? FindNodeBefore(Node node, int pos, object? test = null,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null, object? state = null)
    {
        var pred = MakeTest(test);
        baseVisitor ??= BaseVisitor;
        Found? max = null;
        Walker c = null!;
        c = new Walker((n, st, ov) =>
        {
            if (n.Start > pos) return;
            string type = ov ?? n.Type;
            if (n.End <= pos && (max == null || max.Node.End < n.End) && pred(type, n))
                max = new Found(n, st);
            VisitNode(baseVisitor, type, n, st, c);
        });
        c.Recurse(node, state);
        return max;
    }

    // Used to create a custom walker. Fills in all missing node type properties
    // with the defaults from baseVisitor (or the built-in base table).
    public static Dictionary<string, WalkFn> Make(IReadOnlyDictionary<string, WalkFn> funcs,
        IReadOnlyDictionary<string, WalkFn>? baseVisitor = null)
    {
        var visitor = new Dictionary<string, WalkFn>((IEnumerable<KeyValuePair<string, WalkFn>>)(baseVisitor ?? BaseVisitor));
        foreach (var kv in funcs)
            visitor[kv.Key] = kv.Value;
        return visitor;
    }

    /// <summary>Recurse into <paramref name="node"/> unchanged (JS <c>skipThrough</c>).</summary>
    public static readonly WalkFn SkipThrough = (node, st, c) => c.Recurse(node, st);

    /// <summary>Do nothing / stop descending (JS <c>ignore</c>).</summary>
    public static readonly WalkFn Ignore = (node, st, c) => { };

    // ------------------------------------------------------------------
    // Node walkers (the base table).
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, WalkFn> _base = BuildBase();

    /// <summary>The base visitor table (JS <c>base</c>).</summary>
    public static IReadOnlyDictionary<string, WalkFn> BaseVisitor => _base;

    // --- small helpers mirroring JS truthy child reads ---
    private static Node AsNode(object? o) => (Node)o!;
    private static List<object?> AsArr(object? o) => (List<object?>)o!;
    private static bool Truthy(object? o) => o is true;

    private static Dictionary<string, WalkFn> BuildBase()
    {
        var b = new Dictionary<string, WalkFn>();

        WalkFn programLike = (node, st, c) =>
        {
            foreach (var stmt in AsArr(node["body"]))
                c.Recurse(AsNode(stmt), st, "Statement");
        };
        b["Program"] = programLike;
        b["BlockStatement"] = programLike;
        b["StaticBlock"] = programLike;

        b["Statement"] = SkipThrough;
        b["EmptyStatement"] = Ignore;

        WalkFn expressionStatement = (node, st, c) => c.Recurse(AsNode(node["expression"]), st, "Expression");
        b["ExpressionStatement"] = expressionStatement;
        b["ParenthesizedExpression"] = expressionStatement;
        b["ChainExpression"] = expressionStatement;

        b["IfStatement"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["test"]), st, "Expression");
            c.Recurse(AsNode(node["consequent"]), st, "Statement");
            if (node["alternate"] != null) c.Recurse(AsNode(node["alternate"]), st, "Statement");
        };
        b["LabeledStatement"] = (node, st, c) => c.Recurse(AsNode(node["body"]), st, "Statement");
        b["BreakStatement"] = Ignore;
        b["ContinueStatement"] = Ignore;
        b["WithStatement"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["object"]), st, "Expression");
            c.Recurse(AsNode(node["body"]), st, "Statement");
        };
        b["SwitchStatement"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["discriminant"]), st, "Expression");
            foreach (var cs in AsArr(node["cases"])) c.Recurse(AsNode(cs), st);
        };
        b["SwitchCase"] = (node, st, c) =>
        {
            if (node["test"] != null) c.Recurse(AsNode(node["test"]), st, "Expression");
            foreach (var cons in AsArr(node["consequent"]))
                c.Recurse(AsNode(cons), st, "Statement");
        };

        WalkFn returnLike = (node, st, c) =>
        {
            if (node["argument"] != null) c.Recurse(AsNode(node["argument"]), st, "Expression");
        };
        b["ReturnStatement"] = returnLike;
        b["YieldExpression"] = returnLike;
        b["AwaitExpression"] = returnLike;

        WalkFn throwLike = (node, st, c) => c.Recurse(AsNode(node["argument"]), st, "Expression");
        b["ThrowStatement"] = throwLike;
        b["SpreadElement"] = throwLike;

        b["TryStatement"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["block"]), st, "Statement");
            if (node["handler"] != null) c.Recurse(AsNode(node["handler"]), st);
            if (node["finalizer"] != null) c.Recurse(AsNode(node["finalizer"]), st, "Statement");
        };
        b["CatchClause"] = (node, st, c) =>
        {
            if (node["param"] != null) c.Recurse(AsNode(node["param"]), st, "Pattern");
            c.Recurse(AsNode(node["body"]), st, "Statement");
        };

        WalkFn whileLike = (node, st, c) =>
        {
            c.Recurse(AsNode(node["test"]), st, "Expression");
            c.Recurse(AsNode(node["body"]), st, "Statement");
        };
        b["WhileStatement"] = whileLike;
        b["DoWhileStatement"] = whileLike;

        b["ForStatement"] = (node, st, c) =>
        {
            if (node["init"] != null) c.Recurse(AsNode(node["init"]), st, "ForInit");
            if (node["test"] != null) c.Recurse(AsNode(node["test"]), st, "Expression");
            if (node["update"] != null) c.Recurse(AsNode(node["update"]), st, "Expression");
            c.Recurse(AsNode(node["body"]), st, "Statement");
        };
        WalkFn forInLike = (node, st, c) =>
        {
            c.Recurse(AsNode(node["left"]), st, "ForInit");
            c.Recurse(AsNode(node["right"]), st, "Expression");
            c.Recurse(AsNode(node["body"]), st, "Statement");
        };
        b["ForInStatement"] = forInLike;
        b["ForOfStatement"] = forInLike;
        b["ForInit"] = (node, st, c) =>
        {
            if (node.Type == "VariableDeclaration") c.Recurse(node, st);
            else c.Recurse(node, st, "Expression");
        };
        b["DebuggerStatement"] = Ignore;

        WalkFn functionDeclaration = (node, st, c) => c.Recurse(node, st, "Function");
        b["FunctionDeclaration"] = functionDeclaration;
        b["VariableDeclaration"] = (node, st, c) =>
        {
            foreach (var decl in AsArr(node["declarations"]))
                c.Recurse(AsNode(decl), st);
        };
        b["VariableDeclarator"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["id"]), st, "Pattern");
            if (node["init"] != null) c.Recurse(AsNode(node["init"]), st, "Expression");
        };

        b["Function"] = (node, st, c) =>
        {
            if (node["id"] != null) c.Recurse(AsNode(node["id"]), st, "Pattern");
            foreach (var param in AsArr(node["params"]))
                c.Recurse(AsNode(param), st, "Pattern");
            c.Recurse(AsNode(node["body"]), st, Truthy(node["expression"]) ? "Expression" : "Statement");
        };

        b["Pattern"] = (node, st, c) =>
        {
            if (node.Type == "Identifier")
                c.Recurse(node, st, "VariablePattern");
            else if (node.Type == "MemberExpression")
                c.Recurse(node, st, "MemberPattern");
            else
                c.Recurse(node, st);
        };
        b["VariablePattern"] = Ignore;
        b["MemberPattern"] = SkipThrough;
        b["RestElement"] = (node, st, c) => c.Recurse(AsNode(node["argument"]), st, "Pattern");
        b["ArrayPattern"] = (node, st, c) =>
        {
            foreach (var elt in AsArr(node["elements"]))
            {
                if (elt != null) c.Recurse(AsNode(elt), st, "Pattern");
            }
        };
        b["ObjectPattern"] = (node, st, c) =>
        {
            foreach (var pobj in AsArr(node["properties"]))
            {
                var prop = AsNode(pobj);
                if (prop.Type == "Property")
                {
                    if (Truthy(prop["computed"])) c.Recurse(AsNode(prop["key"]), st, "Expression");
                    c.Recurse(AsNode(prop["value"]), st, "Pattern");
                }
                else if (prop.Type == "RestElement")
                {
                    c.Recurse(AsNode(prop["argument"]), st, "Pattern");
                }
            }
        };

        b["Expression"] = SkipThrough;
        b["ThisExpression"] = Ignore;
        b["Super"] = Ignore;
        b["MetaProperty"] = Ignore;
        b["ArrayExpression"] = (node, st, c) =>
        {
            foreach (var elt in AsArr(node["elements"]))
            {
                if (elt != null) c.Recurse(AsNode(elt), st, "Expression");
            }
        };
        b["ObjectExpression"] = (node, st, c) =>
        {
            foreach (var prop in AsArr(node["properties"]))
                c.Recurse(AsNode(prop), st);
        };
        b["FunctionExpression"] = functionDeclaration;
        b["ArrowFunctionExpression"] = functionDeclaration;
        b["SequenceExpression"] = (node, st, c) =>
        {
            foreach (var expr in AsArr(node["expressions"]))
                c.Recurse(AsNode(expr), st, "Expression");
        };
        b["TemplateLiteral"] = (node, st, c) =>
        {
            foreach (var quasi in AsArr(node["quasis"]))
                c.Recurse(AsNode(quasi), st);

            foreach (var expr in AsArr(node["expressions"]))
                c.Recurse(AsNode(expr), st, "Expression");
        };
        b["TemplateElement"] = Ignore;
        WalkFn unaryLike = (node, st, c) => c.Recurse(AsNode(node["argument"]), st, "Expression");
        b["UnaryExpression"] = unaryLike;
        b["UpdateExpression"] = unaryLike;
        WalkFn binaryLike = (node, st, c) =>
        {
            c.Recurse(AsNode(node["left"]), st, "Expression");
            c.Recurse(AsNode(node["right"]), st, "Expression");
        };
        b["BinaryExpression"] = binaryLike;
        b["LogicalExpression"] = binaryLike;
        WalkFn assignLike = (node, st, c) =>
        {
            c.Recurse(AsNode(node["left"]), st, "Pattern");
            c.Recurse(AsNode(node["right"]), st, "Expression");
        };
        b["AssignmentExpression"] = assignLike;
        b["AssignmentPattern"] = assignLike;
        b["ConditionalExpression"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["test"]), st, "Expression");
            c.Recurse(AsNode(node["consequent"]), st, "Expression");
            c.Recurse(AsNode(node["alternate"]), st, "Expression");
        };
        WalkFn callLike = (node, st, c) =>
        {
            c.Recurse(AsNode(node["callee"]), st, "Expression");
            if (node["arguments"] != null)
                foreach (var arg in AsArr(node["arguments"]))
                    c.Recurse(AsNode(arg), st, "Expression");
        };
        b["NewExpression"] = callLike;
        b["CallExpression"] = callLike;
        b["MemberExpression"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["object"]), st, "Expression");
            if (Truthy(node["computed"])) c.Recurse(AsNode(node["property"]), st, "Expression");
        };
        WalkFn exportDecl = (node, st, c) =>
        {
            if (node["declaration"] != null)
                c.Recurse(AsNode(node["declaration"]), st,
                    node.Type == "ExportNamedDeclaration" || AsNode(node["declaration"])["id"] != null ? "Statement" : "Expression");
            if (node["source"] != null) c.Recurse(AsNode(node["source"]), st, "Expression");
            if (node["attributes"] != null)
                foreach (var attr in AsArr(node["attributes"]))
                    c.Recurse(AsNode(attr), st);
        };
        b["ExportNamedDeclaration"] = exportDecl;
        b["ExportDefaultDeclaration"] = exportDecl;
        b["ExportAllDeclaration"] = (node, st, c) =>
        {
            if (node["exported"] != null)
                c.Recurse(AsNode(node["exported"]), st);
            c.Recurse(AsNode(node["source"]), st, "Expression");
            if (node["attributes"] != null)
                foreach (var attr in AsArr(node["attributes"]))
                    c.Recurse(AsNode(attr), st);
        };
        b["ImportAttribute"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["value"]), st, "Expression");
        };
        b["ImportDeclaration"] = (node, st, c) =>
        {
            foreach (var spec in AsArr(node["specifiers"]))
                c.Recurse(AsNode(spec), st);
            c.Recurse(AsNode(node["source"]), st, "Expression");
            if (node["attributes"] != null)
                foreach (var attr in AsArr(node["attributes"]))
                    c.Recurse(AsNode(attr), st);
        };
        b["ImportExpression"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["source"]), st, "Expression");
            if (node["options"] != null) c.Recurse(AsNode(node["options"]), st, "Expression");
        };
        b["ImportSpecifier"] = Ignore;
        b["ImportDefaultSpecifier"] = Ignore;
        b["ImportNamespaceSpecifier"] = Ignore;
        b["Identifier"] = Ignore;
        b["PrivateIdentifier"] = Ignore;
        b["Literal"] = Ignore;

        b["TaggedTemplateExpression"] = (node, st, c) =>
        {
            c.Recurse(AsNode(node["tag"]), st, "Expression");
            c.Recurse(AsNode(node["quasi"]), st, "Expression");
        };
        WalkFn classDeclaration = (node, st, c) => c.Recurse(node, st, "Class");
        b["ClassDeclaration"] = classDeclaration;
        b["ClassExpression"] = classDeclaration;
        b["Class"] = (node, st, c) =>
        {
            if (node["id"] != null) c.Recurse(AsNode(node["id"]), st, "Pattern");
            if (node["superClass"] != null) c.Recurse(AsNode(node["superClass"]), st, "Expression");
            c.Recurse(AsNode(node["body"]), st);
        };
        b["ClassBody"] = (node, st, c) =>
        {
            foreach (var elt in AsArr(node["body"]))
                c.Recurse(AsNode(elt), st);
        };
        WalkFn methodDefinition = (node, st, c) =>
        {
            if (Truthy(node["computed"])) c.Recurse(AsNode(node["key"]), st, "Expression");
            if (node["value"] != null) c.Recurse(AsNode(node["value"]), st, "Expression");
        };
        b["MethodDefinition"] = methodDefinition;
        b["PropertyDefinition"] = methodDefinition;
        b["Property"] = methodDefinition;

        return b;
    }
}

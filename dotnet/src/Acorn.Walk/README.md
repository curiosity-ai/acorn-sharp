# Acorn.Walk

A C# port of [acorn-walk](https://github.com/acornjs/acorn/tree/master/acorn-walk),
the syntax-tree walker for ESTree trees produced by the **Acorn** parser.
Targets .NET 10.

```csharp
using Acorn;
using Acorn.Walk;

Node ast = Parser.ParseStatic("f(1); g(2);", new Options { EcmaVersion = 2020 });

AstWalker.Simple(ast, new Dictionary<string, Action<Node, object?>>
{
    ["CallExpression"] = (node, state) =>
        Console.WriteLine("call at " + node.Start)
});
```

Provides `AstWalker.Simple`, `Ancestor`, `Recursive`, `Full`, `FullAncestor`,
`FindNodeAt`, `FindNodeAround`, `FindNodeAfter`, `FindNodeBefore`, `Make`, and
the `BaseVisitor` table.

MIT licensed, like upstream Acorn.

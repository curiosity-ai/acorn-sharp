# Acorn

A C# port of the [Acorn](https://github.com/acornjs/acorn) JavaScript parser,
targeting .NET 10. Parses JavaScript/ECMAScript into
[ESTree](https://github.com/estree/estree)-compatible syntax trees.

```csharp
using Acorn;

Node ast = Parser.ParseStatic("let answer = 6 * 7;",
    new Options { EcmaVersion = 2020, Locations = true });

Console.WriteLine(ast.Type); // "Program"
```

- `Parser.ParseStatic(input, options)` — parse a program.
- `Parser.ParseExpressionAt(input, pos, options)` — parse one expression.
- `Parser.Tokenizer(input, options)` then `GetToken()` — tokenize.

`Node` is a property bag: `Type`, `Start`, `End`, `Loc`, `Range` are members;
all other properties are reached via the indexer (`node["body"]`, etc.). Child
arrays are `List<object?>`.

See the companion packages **Acorn.Loose** (error-tolerant parsing) and
**Acorn.Walk** (AST traversal).

MIT licensed, like upstream Acorn.

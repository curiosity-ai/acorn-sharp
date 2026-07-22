# Acorn for .NET

A C# port of the [Acorn](https://github.com/acornjs/acorn) JavaScript parser,
targeting **.NET 10**. It parses JavaScript/ECMAScript source into
[ESTree](https://github.com/estree/estree)-compatible abstract syntax trees —
the same trees the original JavaScript library produces.

This is a fork/port. The original JavaScript sources are kept in the repository
root (`acorn/`, `acorn-loose/`, `acorn-walk/`) so the port can be kept in sync
with upstream; all C# code lives under `dotnet/`.

## Packages

| Package        | Description                                             |
|----------------|---------------------------------------------------------|
| `Acorn`        | The main parser and tokenizer.                          |
| `Acorn.Loose`  | An error-tolerant parser that repairs broken input.     |
| `Acorn.Walk`   | A syntax-tree walker for ESTree trees.                  |

## Usage

```csharp
using Acorn;

// Parse a whole program
var options = new Options { EcmaVersion = 2020, Locations = true };
Node ast = Parser.ParseStatic("let answer = 6 * 7;", options);

Console.WriteLine(ast.Type);                 // "Program"
var body = (List<object?>)ast["body"]!;
var decl = (Node)body[0]!;
Console.WriteLine(decl.Type);                // "VariableDeclaration"

// Parse a single expression at an offset
Node expr = Parser.ParseExpressionAt("1 + 2", 0, new Options { EcmaVersion = 2020 });

// Tokenize
var tokenizer = Parser.Tokenizer("a + b", new Options { EcmaVersion = 2020 });
for (Token t = tokenizer.GetToken(); t.Type != TokenTypes.Eof; t = tokenizer.GetToken())
    Console.WriteLine($"{t.Type.Label} {t.Start}..{t.End}");
```

### Options

`Acorn.Options` mirrors the JavaScript options: `EcmaVersion` (an int such as
`5`, `2015`, `2020`, or `Options.Latest`), `SourceType` (`"script"`,
`"module"`, `"commonjs"`), `Locations`, `Ranges`, `PreserveParens`,
`AllowReturnOutsideFunction`, `AllowImportExportEverywhere`,
`AllowAwaitOutsideFunction`, `AllowReserved`, `OnComment`, `OnToken`, and more.

### The AST (`Node`)

Because acorn assigns node properties dynamically, `Node` is a property bag:
`Type`, `Start`, `End`, `Loc`, `Range` are strongly-typed members; all other
properties are reached through the string indexer, e.g. `node["name"]`,
`node["body"]`, `node["value"]`. Child arrays are `List<object?>`; nested
locations are `SourceLocation`/`Position`.

### Loose parsing

```csharp
using Acorn.Loose;

Node ast = AcornLoose.Parse("foo(", new Options { EcmaVersion = 2020 });
// Produces a best-effort tree instead of throwing.
```

### Walking

```csharp
using Acorn.Walk;

Walk.Simple(ast, new Dictionary<string, Action<Node, object?>>
{
    ["CallExpression"] = (node, state) => Console.WriteLine("call at " + node.Start)
});
```

## Building & testing

```bash
cd dotnet
dotnet build Acorn.sln -c Release
dotnet test test/Acorn.Tests/Acorn.Tests.csproj
```

The test suite runs the C# parser against **3530 conformance cases** generated
from acorn's own JavaScript test suite (see `../CLAUDE.md` for how). To
regenerate the fixtures after an upstream sync:

```bash
node tools/gen-fixtures.js
```

## License

MIT, the same as upstream Acorn. See `../acorn/LICENSE`.

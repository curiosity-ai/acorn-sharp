# Acorn C# Port — Project Guide

This repository is a fork of the [Acorn](https://github.com/acornjs/acorn)
JavaScript parser. In addition to the upstream JavaScript sources (kept intact
so the fork can track upstream), it contains a faithful **C# port targeting
.NET 10**, living entirely under [`dotnet/`](dotnet/).

## Why the port exists

Acorn is a small, fast, standards-compliant JavaScript parser that produces
[ESTree](https://github.com/estree/estree)-compatible ASTs. This port makes the
same parser available to .NET applications (Curiosity et al.) without a
JavaScript runtime, published as NuGet packages.

## Repository layout

```
acorn/            Upstream JS: the main parser (unchanged)
acorn-loose/      Upstream JS: the error-tolerant parser (unchanged)
acorn-walk/       Upstream JS: the AST walker (unchanged)
test/             Upstream JS test suite (source of the C# conformance corpus)
dotnet/           >>> The C# port <<<
  src/Acorn/            Core parser + tokenizer + regexp validator
  src/Acorn.Loose/      Error-tolerant parser (LooseParser)
  src/Acorn.Walk/       AST walker
  test/Acorn.Conformance/  Console runner over the fixture corpus
  test/Acorn.Tests/        xUnit tests (Normal / Normal+commonjs / Loose)
  fixtures/all.json     3530 test cases generated from the JS test suite
  tools/                Fixture / data generators (Node.js)
  .devops/build-nuget.yml  Azure DevOps pipeline (builds & packs the packages)
  PORTING-CONVENTIONS.md   The rules the port follows (read this)
.github/workflows/    Upstream CI, disabled (renamed *.disable)
```

## How the C# port maps to the JS source

The JS parser is one `Parser` class whose prototype is extended across many
files. The C# port mirrors this with a single `public partial class Parser`
(namespace `Acorn`) split across `Parser.*.cs` files:

| JavaScript source        | C# file(s)                          |
|--------------------------|-------------------------------------|
| `state.js`               | `Parser.State.cs`, `ParserTypes.cs` |
| `node.js`                | `Parser.Node.cs`, `Node.cs`         |
| `tokenize.js`            | `Parser.Tokenize.cs`, `Token.cs`    |
| `tokentype.js`           | `TokenType.cs`                      |
| `tokencontext.js`        | `TokContext.cs`, `Parser.TokenContext.cs` |
| `statement.js`           | `Parser.Statement.cs`               |
| `expression.js`          | `Parser.Expression.cs`              |
| `lval.js`                | `Parser.Lval.cs`                    |
| `scope.js`/`scopeflags.js`| `Parser.Scope.cs`, `ScopeFlags.cs` |
| `parseutil.js`           | `Parser.ParseUtil.cs`               |
| `location.js`/`locutil.js`| `Parser.Location.cs`, `Locutil.cs` |
| `options.js`             | `Options.cs`                        |
| `regexp.js`              | `Parser.RegExp.cs`, `RegExpValidationState.cs`, `UnicodePropertyData.cs` |
| `identifier.js` + generated | `Identifier.cs`, `GeneratedIdentifierData.cs` |
| `whitespace.js`/`util.js`| `Whitespace.cs`, `Util.cs`          |
| `acorn-loose/*`          | `src/Acorn.Loose/*`                 |
| `acorn-walk/*`           | `src/Acorn.Walk/Walk.cs`            |

### Key design decision: the AST is a property bag

Acorn assigns node properties dynamically. `Acorn.Node` models this: `Type`,
`Start`, `End`, `Loc`, `Range` are fields; every other property is stored in a
backing dictionary reached through the string indexer (`node["name"]`). This
keeps the port faithful and produces ESTree-identical trees. See
[`PORTING-CONVENTIONS.md`](dotnet/PORTING-CONVENTIONS.md) for the full contract.

## Testing strategy

Hand-porting the 73k-line JS test suite is infeasible, so
`dotnet/tools/gen-fixtures.js` loads the original JS test files, captures every
`test()`/`testFail()` case, and serializes them (code + expected ESTree AST +
options) to `dotnet/fixtures/all.json` (3530 cases). `RegExp` and `BigInt`
values are encoded with `$regexp`/`$bigint` markers.

`dotnet/test/Acorn.Conformance/Comparator.cs` re-implements the JS driver's
`misMatch` structural comparison. The C# parser is run over every fixture in
Normal, Normal+commonjs and Loose modes and its output compared to the expected
tree — the same coverage the JavaScript project has.

## Common commands

```bash
# Regenerate fixtures from the JS test suite (after an upstream sync)
node dotnet/tools/gen-fixtures.js
node dotnet/tools/gen-identifier-data.js   # regenerate Unicode identifier data

# Build & test the C# port
cd dotnet
dotnet build Acorn.sln -c Release
dotnet test test/Acorn.Tests/Acorn.Tests.csproj      # xUnit conformance
dotnet run --project test/Acorn.Conformance -c Release # fast console run (writes conformance-failures.txt)
```

## Syncing with upstream

The JS sources under `acorn/`, `acorn-loose/`, `acorn-walk/`, `test/` are kept
untouched so upstream changes can be merged. After a sync: regenerate fixtures
and identifier data, then re-run the conformance tests and update the C# port to
cover any new syntax.

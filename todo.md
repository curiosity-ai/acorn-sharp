# Acorn → C# port — TODO / status

Porting the Acorn JavaScript parser to C# (.NET 10), under `dotnet/`, keeping the
upstream JS sources intact for future syncs.

## Done

- [x] Analyse the repo (acorn / acorn-loose / acorn-walk / test suite).
- [x] Decide architecture: `partial class Parser`, property-bag `Node`,
      conformance testing via fixtures generated from the JS test suite.
- [x] Scaffold `dotnet/` (src + test + tools + .devops).
- [x] Fixture generator (`tools/gen-fixtures.js`) → `fixtures/all.json` (3530 cases).
- [x] Unicode identifier-data generator (`tools/gen-identifier-data.js`).
- [x] Foundation types: `Node`, `TokenType`/`TokenTypes`, `TokContext`,
      `Options`, `Position`/`SourceLocation`, `Identifier`, `Util`, `Whitespace`,
      `ScopeFlags`, `ParserTypes`.
- [x] Tokenizer + parser state (`state.js`, `tokenize.js`, `parseutil.js`,
      `scope.js`, `location.js`, `node.js`, `tokencontext.js`).
- [x] RegExp validator (`regexp.js`) + Unicode property data.
- [x] `acorn-walk` port.
- [x] Statement / expression / lval parser core.
- [x] `acorn-loose` port.
- [x] Conformance harness + comparator (Normal / Normal+commonjs / Loose).
- [x] xUnit test project.
- [x] Docs: `CLAUDE.md`, `dotnet/README.md`, per-package READMEs, this file.
- [x] Disable upstream CI (`.github/workflows/ci.yml` → `.yml.disable`).
- [x] Azure DevOps pipeline (`dotnet/.devops/build-nuget.yml`).
- [x] Solution file, green build, conformance passing.

## Maintenance / future work

- [ ] Keep in sync with upstream acorn: re-run `gen-fixtures.js` /
      `gen-identifier-data.js` after merging upstream, then close any conformance
      gaps for newly-added syntax.
- [ ] Optional: add `onToken`/`onComment` streaming ergonomics and an
      `IEnumerable<Token>` tokenizer wrapper.
- [ ] Optional: benchmarks vs. the JS parser.

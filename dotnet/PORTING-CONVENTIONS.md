# Acorn → C# porting conventions (READ FIRST)

This is a **faithful, line-by-line port** of the JavaScript Acorn parser to C#
targeting **.NET 10**. Fidelity to the original algorithm matters more than
"idiomatic" rewrites — keep the same control flow, the same method names, the
same order of operations. The C# must produce **byte-for-byte identical ESTree
ASTs** (verified against 3530 real conformance fixtures).

## Project layout

- Core library namespace: `Acorn`, project `dotnet/src/Acorn`.
- `Parser` is a single `public partial class Parser` split across files
  `Parser.<Area>.cs`. Every method you port becomes an instance method on this
  partial class (JS `pp.foo = function(...)` → `public Node Foo(...) { ... }`).

## Foundation already written (DO NOT redefine — call these)

These files already exist and compile. Use their exact APIs:

- `Node` (Node.cs): property bag. Fields: `Type` (string), `Start` (int),
  `End` (int), `Loc` (SourceLocation?), `Range` (int[]?), `SourceFile`.
  **All other properties via string indexer**: `node["name"]`, `node["body"]`,
  etc. `node.Has("x")` tests presence; `node.Remove("x")` deletes.
- `TokenType`, `TokenTypes` (aliased `tt`). See mapping table below.
- `TokContext`, `TokContexts` (aliased `tc`).
- `Options` (fields PascalCased: `EcmaVersion` (int; `Options.Latest` = latest),
  `SourceType`, `Locations`, `Ranges`, `PreserveParens`, `AllowReserved`
  (enum `AllowReservedOption`), `AllowAwaitOutsideFunction` (bool?),
  `AllowSuperOutsideMethod` (bool?), `AllowReturnOutsideFunction`,
  `AllowImportExportEverywhere`, `CheckPrivateFields`, `AllowHashBang` (bool?),
  `Program`, `OnComment`, `OnToken`, `SourceFile`, `DirectSourceFile`).
- `Position`, `SourceLocation`, `LocUtil.GetLineInfo`.
- `Identifier.IsIdentifierStart/IsIdentifierChar/ReservedWords/Keywords/KeywordRelationalOperator`.
- `Util.WordsRegexp/CodePointToString/LoneSurrogate`.
- `Whitespace.LineBreak/IsNewLine/NextLineBreak/SkipWhiteSpace/NonASCIIwhitespace`.
- `ScopeFlags.*` constants, `ScopeFlags.FunctionFlags(async,gen)`, `BindFlags.*`.
- Parser state fields (Parser.State.cs): `Options`, `Input`, `Pos`, `Type`
  (current token type, a `TokenType`), `Value` (current token value, `object?`),
  `Start`, `End`, `StartLoc`, `EndLoc`, `LastTokStart`, `LastTokEnd`,
  `LastTokStartLoc`, `LastTokEndLoc`, `Context`, `ExprAllowed`, `InModule`,
  `Strict`, `ContainsEsc`, `PotentialArrowAt`, `PotentialArrowInForAwait`,
  `YieldPos`, `AwaitPos`, `AwaitIdentPos`, `Labels` (List<LabelInfo>),
  `UndefinedExports` (Dictionary<string,Node>), `ScopeStack` (List<Scope>),
  `RegexpState`, `PrivateNameStack` (List<PrivateNameStatus>),
  `Keywords`/`ReservedWords`/`ReservedWordsStrict`/`ReservedWordsStrictBind` (Regex).
- Parser getters (properties): `InFunction`, `InGenerator`, `InAsync`,
  `CanAwait`, `AllowReturn`, `AllowSuper`, `AllowDirectSuper`,
  `TreatFunctionsAsVar`, `AllowNewDotTarget`, `AllowUsing`, `InClassStaticBlock`.
- Parser helper methods (foundation): `StartNode()`, `StartNodeAt(pos,loc)`,
  `FinishNode(node,type)`, `FinishNodeAt(node,type,pos,loc)`, `CopyNode(node)`,
  `Raise(pos,msg)`, `RaiseRecoverable(pos,msg)`, `CurPosition()`,
  `EnterScope(flags)`, `ExitScope()`, `TreatFunctionsAsVarInScope(scope)`,
  `DeclareName(name,bindingType,pos)`, `CheckLocalExport(id)`, `CurrentScope()`,
  `CurrentVarScope()`, `CurrentThisScope()`, `StrictDirective(start)`,
  `Eat(type)`, `IsContextual(name)`, `EatContextual(name)`,
  `ExpectContextual(name)`, `CanInsertSemicolon()`, `InsertSemicolon()`,
  `Semicolon()`, `AfterTrailingComma(tokType, notNext=false)`, `Expect(type)`,
  `Unexpected(pos=null)`, `CheckPatternErrors(refDE,isAssign)`,
  `CheckExpressionErrors(refDE,andThrow=false)`, `CheckYieldAwaitInDefaultParams()`,
  `IsSimpleAssignTarget(expr)`, `CatchStackOverflow(f)`.
- Tokenizer methods (foundation): `Next(ignoreEsc=false)`, `NextToken()`,
  `GetToken()`, `ReadWord1()`, `ReadRegexp()`, plus `CurContext()`,
  `PushContext(c)`, `PopContext()`, `BraceIsBlock(prevType)`,
  `InGeneratorContext()`, `UpdateContext(prevType)`, `OverrideContext(ctx)`,
  `InitialContext()`.
- Helper classes: `Scope` (Flags,Var,Lexical,Functions lists), `LabelInfo`
  (Name,Kind,StatementStart), `PrivateNameStatus` (Declared Dictionary<string,string?>,
  Used List<Node>), `DestructuringErrors` (ShorthandAssign, TrailingComma,
  ParenthesizedAssign, ParenthesizedBind, DoubleProto — all int, init -1).
- Exceptions: `AcornSyntaxError` (thrown by Raise).

## Required `using` aliases at the top of every parser file

```csharp
using System.Numerics;
using tt = Acorn.TokenTypes;
using tc = Acorn.TokContexts;
using static Acorn.ScopeFlags;
using static Acorn.BindFlags;

namespace Acorn;

public partial class Parser
{
    // ... your ported methods ...
}
```

## Token type name mapping (JS `tt.x` → C# `tt.X`)

Non-keywords: num→Num, regexp→Regexp, string→String, name→Name,
privateId→PrivateId, eof→Eof, bracketL→BracketL, bracketR→BracketR,
braceL→BraceL, braceR→BraceR, parenL→ParenL, parenR→ParenR, comma→Comma,
semi→Semi, colon→Colon, dot→Dot, question→Question, questionDot→QuestionDot,
arrow→Arrow, template→Template, invalidTemplate→InvalidTemplate,
ellipsis→Ellipsis, backQuote→BackQuote, dollarBraceL→DollarBraceL, eq→Eq,
assign→Assign, incDec→IncDec, prefix→Prefix, logicalOR→LogicalOR,
logicalAND→LogicalAND, bitwiseOR→BitwiseOR, bitwiseXOR→BitwiseXOR,
bitwiseAND→BitwiseAND, equality→Equality, relational→Relational,
bitShift→BitShift, plusMin→PlusMin, modulo→Modulo, star→Star, slash→Slash,
starstar→StarStar, coalesce→Coalesce.

Keywords (drop the leading underscore, PascalCase): _break→Break, _case→Case,
_catch→Catch, _continue→Continue, _debugger→Debugger, _default→Default, _do→Do,
_else→Else, _finally→Finally, _for→For, _function→Function, _if→If,
_return→Return, _switch→Switch, _throw→Throw, _try→Try, _var→Var, _const→Const,
_while→While, _with→With, _new→New, _this→This, _super→Super, _class→Class,
_extends→Extends, _export→Export, _import→Import, _null→Null, _true→True,
_false→False, _in→In, _instanceof→InstanceOf, _typeof→TypeOf, _void→Void,
_delete→Delete.

TokContext (JS `types.x`/`tokContexts` → C# `tc.X`): b_stat→BStat, b_expr→BExpr,
b_tmpl→BTmpl, p_stat→PStat, p_expr→PExpr, q_tmpl→QTmpl, f_stat→FStat,
f_expr→FExpr, f_expr_gen→FExprGen, f_gen→FGen.

## Method naming

Every `pp.methodName` becomes `MethodName` (PascalCase, keep the rest of the
name identical). Keep the same parameters in the same order. Examples:
`parseStatement(context, topLevel, exports)` → `ParseStatement(...)`,
`parseExprSubscripts(refDestructuringErrors, forInit)` → `ParseExprSubscripts(...)`.

## Type conventions for the port

- **AST child arrays**: use `List<object?>`. Create a local, assign to the node,
  then add:
  ```csharp
  var body = new List<object?>();
  node["body"] = body;
  // ...
  body.Add(ParseStatement(...));
  ```
  Elisions (holes in array literals) are `null` entries — `List<object?>` handles
  them. When you need to read back and iterate, cast: `foreach (var el in (List<object?>)node["elements"]!) { ... }`.
- **Inline plain object literals** that are NOT AST nodes (e.g.
  `node.regex = {pattern, flags}`, TemplateElement `value = {raw, cooked}`):
  use `new Dictionary<string, object?> { ["pattern"] = ..., ["flags"] = ... }`.
- **`this.value`** (current token value) is `object?`. Cast when needed:
  `(string)Value!` for names/strings; for numbers it is a `double` or a
  `System.Numerics.BigInteger`; for a regexp token it is a `RegexpTokenValue`
  (fields `Pattern`, `Flags`, `Value`).
- **Numbers**: numeric literal values are `double`; BigInt literal values are
  `System.Numerics.BigInteger`.
- Node property reads return `object?`; cast to `Node`: `(Node)node["id"]!` or
  `node["id"] as Node`. Test `node.prop != null` → `node["prop"] != null`.
  JS `"prop" in node` → `node.Has("prop")`. `delete node.prop` → `node.Remove("prop")`.
- Booleans stored on nodes: JS truthy stored values → store real C# `bool`.
- `this.type === tt.x` → `Type == tt.X`. `this.type !== tt.x` → `Type != tt.X`.

## Common JS → C# idiom mapping

- `let`/`const`/`var` → `var` or explicit type. `arr.push(x)` → `list.Add(x)`.
  `arr.length` → `list.Count`. `arr.indexOf(x)` → `list.IndexOf(x)`.
- `str.slice(a,b)` → `Input.Substring(a, b - a)` (mind JS slice semantics; when
  `b` omitted, to end). `str.charCodeAt(i)` — there is a private helper pattern;
  for parser code prefer `Input[i]` guarded by length, matching the JS which
  already guards. `str.charAt(i)` → `i < s.Length ? s[i] : '\0'`.
- Regex: `re.test(s)` → `re.IsMatch(s)`; `re.exec(s)` → `re.Match(s)`.
- `for (let x of arr)` → `foreach (var x in arr)`.
- Object as map (`Object.create(null)`) → `Dictionary<string, T>`.
- String template literals `` `a${b}` `` → `$"a{b}"` or concatenation.
- `throw new SyntaxError(...)` is done via `Raise(pos, msg)`; never construct
  errors directly — call `Raise`/`RaiseRecoverable`.
- Optional/default parameters: replicate JS defaults with C# default parameter
  values where the default is a constant; if the default is `this.startNode()`
  (a call), use `Node? node = null` and `node ??= StartNode();` inside.
- Destructured params like `function({isTagged})` → make it a normal parameter
  `bool isTagged` (or pass the object). `function({start, end, name})` → accept
  the `Node`/object and read fields inside.

## Regexp module contract (Parser.RegExp.cs / RegExpValidationState)

The tokenizer calls, and you must provide (as `public partial class Parser`):
`public void ValidateRegExpFlags(RegExpValidationState state)` and
`public void ValidateRegExpPattern(RegExpValidationState state)`.
`RegExpValidationState` is a class constructed as `new RegExpValidationState(Parser parser)`
with `public void Reset(int start, string pattern, string flags)`.

## Verification

The whole thing is validated by `dotnet/test/Acorn.Tests` which loads
`dotnet/fixtures/all.json` (3530 real acorn test cases) and compares the C#
AST against the expected ESTree tree, in both Normal and Loose modes. Aim for
zero regressions vs. the original.

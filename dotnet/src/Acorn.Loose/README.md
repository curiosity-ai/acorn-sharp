# Acorn.Loose

A C# port of [acorn-loose](https://github.com/acornjs/acorn/tree/master/acorn-loose),
the error-tolerant variant of the **Acorn** JavaScript parser. It tries to parse
anything as JavaScript, repairing syntax errors as best it can, producing a
mostly-valid ESTree tree instead of throwing. Targets .NET 10.

```csharp
using Acorn;
using Acorn.Loose;

// Incomplete / broken input still yields a best-effort tree.
Node ast = AcornLoose.Parse("foo(bar,", new Options { EcmaVersion = 2020 });
```

The recommended pattern is to try `Acorn.Parser.ParseStatic` first, and fall
back to `AcornLoose.Parse` only when it throws.

MIT licensed, like upstream Acorn.

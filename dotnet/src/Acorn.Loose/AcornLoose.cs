using Acorn;

namespace Acorn.Loose;

// Acorn: Loose parser
//
// This module provides an alternative parser that exposes the same interface as
// the main module's `parse` function, but will try to parse anything as
// JavaScript, repairing syntax errors the best it can. There are circumstances
// in which it will raise an error and give up, but they are very rare. The
// resulting AST will be a mostly valid JavaScript AST (as per the ESTree spec),
// except that:
//
// - Return outside functions is allowed
// - Label consistency (no conflicts, break only to existing labels) is not enforced.
// - Bogus Identifier nodes with a name of "✖" are inserted whenever the parser
//   got too confused to return anything meaningful.
//
// Ported from acorn-loose/src/index.js.
public static class AcornLoose
{
    public static Node Parse(string input, Options? options = null)
        => LooseParser.Parse(input, options);
}

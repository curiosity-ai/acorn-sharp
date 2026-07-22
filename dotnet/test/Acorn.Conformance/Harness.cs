using System.Text;
using System.Text.Json;
using Acorn;

namespace Acorn.Conformance;

public sealed class ModeResult
{
    public string Name = "";
    public int Run;
    public int Failed;
    public int Crashed;
    public readonly List<string> Failures = new();
}

public sealed class Mode
{
    public string Name = "";
    public Func<string, Options?, Node> Parse = null!;
    public bool Loose;
    public Func<TestCase, bool>? Filter;
    public bool Commonjs;
}

public static class Harness
{
    public static ModeResult Run(Mode mode, List<TestCase> tests, int maxFailuresToRecord = 60)
    {
        var res = new ModeResult { Name = mode.Name };
        foreach (var test in tests)
        {
            if (mode.Filter != null && !mode.Filter(test)) continue;
            res.Run++;

            Options opts = OptionsMapper.Build(test.Options, out var expComments, out var expTokens,
                collectComments: !mode.Loose, collectTokens: !mode.Loose);
            if (mode.Commonjs) opts.SourceType = "commonjs";

            Node ast;
            try
            {
                ast = mode.Parse(test.Code, opts);
            }
            catch (Exception raw)
            {
                Exception e = raw is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : raw;
                if (test.IsFail)
                {
                    // driver: match by message regardless of error type
                    if (ErrorMatches(test.Error!, e.Message)) { /* ok */ }
                    else Fail(res, test, $"Expected error message: {test.Error}\nGot error message: {e.Message}", maxFailuresToRecord);
                }
                else if (e is AcornSyntaxError)
                {
                    // non-fail test raised a genuine SyntaxError
                    Fail(res, test, "Unexpected SyntaxError: " + e.Message, maxFailuresToRecord);
                }
                else
                {
                    Crash(res, test, e.GetType().Name + ": " + e.Message, maxFailuresToRecord);
                }
                continue;
            }

            if (test.IsFail)
            {
                if (mode.Loose) { /* loose auto-ok when parse succeeds on an error test */ }
                else Fail(res, test, $"Expected error message: {test.Error}\nBut parsing succeeded.", maxFailuresToRecord);
                continue;
            }

            if (test.Ast == null) continue;

            string? mis = Comparator.MisMatch(test.Ast.Value, ast);
            if (mis == null && expComments != null && opts.OnCommentList != null)
                mis = Comparator.MisMatch(expComments.Value, opts.OnCommentList.Cast<object?>().ToList());
            if (mis == null && expTokens != null && opts.OnTokenList != null)
                mis = Comparator.MisMatch(expTokens.Value, opts.OnTokenList.Cast<object?>().ToList());

            if (mis != null) Fail(res, test, mis, maxFailuresToRecord);
        }
        return res;
    }

    private static void Fail(ModeResult res, TestCase test, string message, int max)
    {
        res.Failed++;
        if (res.Failures.Count < max)
            res.Failures.Add($"[{test.Group}] {Trunc(test.Code)}\n    {message}");
    }

    private static void Crash(ModeResult res, TestCase test, string message, int max)
    {
        res.Failed++;
        res.Crashed++;
        if (res.Failures.Count < max)
            res.Failures.Add($"[{test.Group}] CRASH {Trunc(test.Code)}\n    {message}");
    }

    private static string Trunc(string code)
    {
        code = code.Replace("\n", "\\n").Replace("\r", "");
        return code.Length > 70 ? code.Substring(0, 70) + "..." : code;
    }

    // driver.js: test.error.charAt(0)==="~" ? indexOf(slice(1))>-1 : e.message===test.error
    private static bool ErrorMatches(string expected, string actual)
    {
        if (expected.Length > 0 && expected[0] == '~')
            return actual.IndexOf(expected.Substring(1), StringComparison.Ordinal) > -1;
        return actual == expected;
    }
}

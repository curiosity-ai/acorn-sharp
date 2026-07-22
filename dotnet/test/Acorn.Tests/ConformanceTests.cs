using System.Text;
using System.Text.Json;
using Acorn;
using Acorn.Conformance;
using Acorn.Loose;
using Xunit;

namespace Acorn.Tests;

/// <summary>
/// Runs the full acorn conformance corpus (3530 cases generated from the
/// original JS test suite) against the C# port in Normal and Loose modes.
/// </summary>
public class ConformanceTests
{
    private static readonly List<TestCase> Tests = Fixtures.Load();

    private static bool OptBool(TestCase t, string key, bool dflt)
    {
        if (t.Options is { } o && o.TryGetProperty(key, out var v))
            return v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && dflt);
        return dflt;
    }

    private static string? OptStr(TestCase t, string key)
        => t.Options is { } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool CommonjsFilter(TestCase t)
    {
        if (!OptBool(t, "commonjs", true)) return false;
        if (OptBool(t, "allowAwaitOutsideFunction", false)) return false;
        var st = OptStr(t, "sourceType");
        return st == null || st == "script";
    }

    [Fact]
    public void NormalMode()
    {
        var mode = new Mode { Name = "Normal", Parse = (code, o) => Parser.ParseStatic(code, o) };
        var report = RunAndReport(mode);
        Assert.True(report.Length == 0, report);
    }

    [Fact]
    public void NormalCommonjsMode()
    {
        var mode = new Mode
        {
            Name = "Normal+commonjs",
            Parse = (code, o) => Parser.ParseStatic(code, o),
            Commonjs = true,
            Filter = CommonjsFilter
        };
        var report = RunAndReport(mode);
        Assert.True(report.Length == 0, report);
    }

    [Fact]
    public void LooseMode()
    {
        var mode = new Mode
        {
            Name = "Loose",
            Parse = (code, o) => AcornLoose.Parse(code, o),
            Loose = true,
            Filter = t => OptBool(t, "loose", true)
        };
        var report = RunAndReport(mode);
        Assert.True(report.Length == 0, report);
    }

    private static string RunAndReport(Mode mode)
    {
        var r = Harness.Run(mode, Tests, maxFailuresToRecord: 40);
        if (r.Failed == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"{mode.Name}: {r.Failed} of {r.Run} failed ({r.Crashed} crashed). First failures:");
        foreach (var f in r.Failures) sb.AppendLine(f);
        return sb.ToString();
    }
}

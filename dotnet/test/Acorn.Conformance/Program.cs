using System.Text;
using System.Text.Json;
using Acorn;
using Acorn.Conformance;

var tests = Fixtures.Load();
Console.WriteLine($"Loaded {tests.Count} test cases.");

bool OptBool(TestCase t, string key, bool dflt)
{
    if (t.Options is { } o && o.TryGetProperty(key, out var v))
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => dflt
        };
    return dflt;
}
string? OptStr(TestCase t, string key)
    => t.Options is { } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

bool LooseFilter(TestCase t) => OptBool(t, "loose", true);
bool CommonjsFilter(TestCase t)
{
    if (!OptBool(t, "commonjs", true)) return false;
    if (OptBool(t, "allowAwaitOutsideFunction", false)) return false;
    var st = OptStr(t, "sourceType");
    return st == null || st == "script";
}

var modes = new List<Mode>
{
    new Mode { Name = "Normal", Parse = (code, o) => Parser.ParseStatic(code, o) },
    new Mode
    {
        Name = "Normal+commonjs",
        Parse = (code, o) => Parser.ParseStatic(code, o),
        Commonjs = true,
        Filter = CommonjsFilter
    },
};

// Loose modes are wired via reflection so this runner also builds before the
// loose parser is present; once Acorn.Loose is referenced they light up.
var looseParse = TryGetLooseParse();
if (looseParse != null)
{
    modes.Add(new Mode { Name = "Loose", Parse = looseParse, Loose = true, Filter = LooseFilter });
    modes.Add(new Mode
    {
        Name = "Loose+commonjs",
        Parse = looseParse,
        Loose = true,
        Commonjs = true,
        Filter = t => LooseFilter(t) && CommonjsFilter(t)
    });
}

int totalFailed = 0;
var sb = new StringBuilder();
foreach (var mode in modes)
{
    var r = Harness.Run(mode, tests);
    totalFailed += r.Failed;
    Console.WriteLine($"{mode.Name,-18}: {r.Run,5} run, {r.Failed,4} failed ({r.Crashed} crashed)");
    sb.AppendLine($"===== {mode.Name}: {r.Run} run, {r.Failed} failed ({r.Crashed} crashed) =====");
    foreach (var f in r.Failures) sb.AppendLine(f);
    sb.AppendLine();
}

string outPath = Path.Combine(AppContext.BaseDirectory, "conformance-failures.txt");
File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"\nTotal failed: {totalFailed}");
Console.WriteLine($"Failure detail: {outPath}");

return totalFailed == 0 ? 0 : 1;

static Func<string, Options?, Node>? TryGetLooseParse()
{
    // Look for Acorn.Loose.AcornLoose.Parse(string, Options) if the assembly is loaded/referenced.
    try
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Acorn.Loose");
        if (asm == null)
        {
            try { asm = System.Reflection.Assembly.Load("Acorn.Loose"); } catch { asm = null; }
        }
        var type = asm?.GetType("Acorn.Loose.AcornLoose");
        var mi = type?.GetMethod("Parse", new[] { typeof(string), typeof(Options) });
        if (mi == null) return null;
        return (code, o) => (Node)mi.Invoke(null, new object?[] { code, o })!;
    }
    catch { return null; }
}

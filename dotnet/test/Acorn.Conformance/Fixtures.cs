using System.Text.Json;

namespace Acorn.Conformance;

public sealed class TestCase
{
    public string Group = "";
    public string Code = "";
    public JsonElement? Ast;
    public string? Error;
    public JsonElement? Options;
    public bool IsFail;
}

public static class Fixtures
{
    private static JsonDocument? _doc;

    public static string LocateFixtureFile()
    {
        // Search a few likely locations relative to the running assembly / cwd.
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", "all.json"),
            Path.Combine(AppContext.BaseDirectory, "all.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "dotnet", "fixtures", "all.json"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // Walk upward looking for dotnet/fixtures/all.json.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "dotnet", "fixtures", "all.json");
            if (File.Exists(p)) return p;
            var p2 = Path.Combine(dir.FullName, "fixtures", "all.json");
            if (File.Exists(p2)) return p2;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate fixtures/all.json");
    }

    /// <summary>Reads a JSON string, decoding the {$str16:[...]} lone-surrogate marker.</summary>
    public static string? ReadJsString(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("$str16", out var arr))
        {
            var sb = new System.Text.StringBuilder();
            foreach (var x in arr.EnumerateArray()) sb.Append((char)x.GetInt32());
            return sb.ToString();
        }
        return null;
    }

    public static List<TestCase> Load()
    {
        string path = LocateFixtureFile();
        string json = File.ReadAllText(path);
        _doc = JsonDocument.Parse(json);
        var list = new List<TestCase>();
        foreach (var el in _doc.RootElement.EnumerateArray())
        {
            var tc = new TestCase
            {
                Group = el.GetProperty("group").GetString() ?? "",
                Code = el.TryGetProperty("code", out var c) ? (ReadJsString(c) ?? "") : "",
                IsFail = el.TryGetProperty("isFail", out var f) && f.ValueKind == JsonValueKind.True,
            };
            if (el.TryGetProperty("ast", out var ast) && ast.ValueKind != JsonValueKind.Undefined)
                tc.Ast = ast;
            if (el.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                tc.Error = ReadJsString(err);
            if (el.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Object)
                tc.Options = opt;
            list.Add(tc);
        }
        return list;
    }
}

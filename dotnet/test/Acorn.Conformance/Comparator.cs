using System.Collections;
using System.Numerics;
using System.Text.Json;
using Acorn;

namespace Acorn.Conformance;

/// <summary>
/// Structural AST comparator mirroring the JavaScript acorn test driver's
/// <c>misMatch</c>. The expected side is a <see cref="JsonElement"/> loaded from
/// the fixtures; the actual side is the C# runtime AST (Node/List/primitives).
/// Returns null on match, or a human-readable mismatch message.
/// </summary>
public static class Comparator
{
    private static readonly object Undefined = new();

    public static string? MisMatch(JsonElement exp, object? act)
    {
        // Special fixture markers.
        if (exp.ValueKind == JsonValueKind.Object)
        {
            if (exp.TryGetProperty("$regexp", out var rx) && exp.EnumerateObject().Count() == 1)
            {
                string want = rx.GetString()!;
                string? got = RegexpToString(act);
                return want == got ? null : $"{want} !== {got}";
            }
            if (exp.TryGetProperty("$bigint", out var bi) && exp.EnumerateObject().Count() == 1)
            {
                string want = bi.GetString()!;
                string got = act is BigInteger b ? b.ToString() : PpJson(act);
                return want == got ? null : $"{want} !== {got}";
            }
        }

        switch (exp.ValueKind)
        {
            case JsonValueKind.Object:
            {
                // JS: object-vs-object. If actual is not object-like, mismatch.
                if (!IsObjectLike(act))
                    return $"{PpJson(exp)} !== {PpJson(act)}";
                foreach (var prop in exp.EnumerateObject())
                {
                    object? sub = GetProp(act!, prop.Name);
                    string? mis = MisMatch(prop.Value, ReferenceEquals(sub, Undefined) ? null : sub);
                    if (ReferenceEquals(sub, Undefined) && prop.Value.ValueKind != JsonValueKind.Null &&
                        !IsMarker(prop.Value) && prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                            or JsonValueKind.True or JsonValueKind.False)
                        mis = $"{PpJson(prop.Value)} !== undefined";
                    if (mis != null) return AddPath(mis, prop.Name);
                }
                return null;
            }
            case JsonValueKind.Array:
            {
                if (act is not IList list || act is byte[])
                    return $"{PpJson(exp)} !== {PpJson(act)}";
                int len = exp.GetArrayLength();
                if (list.Count != len)
                    return $"array length mismatch {len} !== {list.Count}";
                for (int i = 0; i < len; i++)
                {
                    string? mis = MisMatch(exp[i], list[i]);
                    if (mis != null) return AddPath(mis, i.ToString());
                }
                return null;
            }
            case JsonValueKind.String:
            {
                string e = exp.GetString()!;
                string? a = act as string;
                return e == a ? null : $"{PpJson(exp)} !== {PpJson(act)}";
            }
            case JsonValueKind.Number:
            {
                if (!TryToDouble(act, out double a))
                    return $"{PpJson(exp)} !== {PpJson(act)}";
                double e = exp.GetDouble();
                return e == a ? null : $"{PpJson(exp)} !== {PpJson(act)}";
            }
            case JsonValueKind.True:
            case JsonValueKind.False:
            {
                bool e = exp.ValueKind == JsonValueKind.True;
                return (act is bool ab && ab == e) ? null : $"{PpJson(exp)} !== {PpJson(act)}";
            }
            case JsonValueKind.Null:
                // JS: null is falsy -> primitive compare exp !== act.
                return act == null ? null : $"null !== {PpJson(act)}";
            default:
                return null;
        }
    }

    private static bool IsMarker(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object &&
        (e.TryGetProperty("$regexp", out _) || e.TryGetProperty("$bigint", out _));

    private static bool IsObjectLike(object? act) =>
        act is IPropertyBag || act is IDictionary<string, object?> || (act is IList && act is not byte[]);

    private static object? GetProp(object act, string name)
    {
        if (act is IPropertyBag bag)
            return bag.TryGetProperty(name, out var v) ? v : Undefined;
        if (act is IDictionary<string, object?> dict)
            return dict.TryGetValue(name, out var v) ? v : Undefined;
        return Undefined;
    }

    private static string? RegexpToString(object? act)
    {
        return act switch
        {
            null => null,
            RegExpLiteralValue r => r.ToString(),
            RegexpTokenValue t => t.Value?.ToString(),
            string s => s,
            _ => act.ToString()
        };
    }

    private static bool TryToDouble(object? act, out double result)
    {
        switch (act)
        {
            case double d: result = d; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case float f: result = f; return true;
            case short sh: result = sh; return true;
            case byte by: result = by; return true;
            default: result = 0; return false;
        }
    }

    private static string AddPath(string str, string pt)
    {
        if (str.Length > 0 && str[^1] == ')')
            return str.Substring(0, str.Length - 1) + "/" + pt + ")";
        return str + " (" + pt + ")";
    }

    private static string PpJson(object? v)
    {
        switch (v)
        {
            case null: return "null";
            case string s: return "\"" + s + "\"";
            case bool b: return b ? "true" : "false";
            case double d: return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case BigInteger bi: return bi.ToString();
            case RegExpLiteralValue r: return r.ToString();
            case Node n: return "[Node " + n.Type + "]";
            case IList list: return "[Array(" + list.Count + ")]";
            default: return v.ToString() ?? "null";
        }
    }

    private static string PpJson(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return "\"" + e.GetString() + "\"";
            case JsonValueKind.Number: return e.GetRawText();
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Null: return "null";
            case JsonValueKind.Object:
                if (e.TryGetProperty("$regexp", out var rx)) return rx.GetString()!;
                if (e.TryGetProperty("$bigint", out var bi)) return bi.GetString()!;
                return "[Object]";
            case JsonValueKind.Array: return "[Array(" + e.GetArrayLength() + ")]";
            default: return e.GetRawText();
        }
    }
}

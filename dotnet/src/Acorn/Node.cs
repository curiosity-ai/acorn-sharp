using System.Numerics;

namespace Acorn;

/// <summary>
/// An ESTree AST node. Acorn assigns node properties dynamically depending on
/// the node type, so this is modelled as a property bag: the five "hot"
/// properties (<c>type</c>, <c>start</c>, <c>end</c>, <c>loc</c>, <c>range</c>)
/// are stored in fields, and any other property is stored in a backing
/// dictionary accessed through the string indexer.
/// </summary>
public sealed class Node : IPropertyBag
{
    public string Type = "";
    public int Start;
    public int End;
    public SourceLocation? Loc;
    public int[]? Range;
    public object? SourceFile;

    private Dictionary<string, object?>? _extra;

    public Node(Parser parser, int pos, Position? loc)
    {
        Start = pos;
        End = 0;
        if (parser.Options.Locations)
            Loc = new SourceLocation(parser, loc, null);
        if (parser.Options.DirectSourceFile != null)
            SourceFile = parser.Options.DirectSourceFile;
        if (parser.Options.Ranges)
            Range = new[] { pos, 0 };
    }

    // Internal, parser-free constructor (used by copyNode-style helpers).
    private Node() { }

    public IReadOnlyDictionary<string, object?> Extra =>
        (IReadOnlyDictionary<string, object?>?)_extra ?? EmptyExtra;

    private static readonly Dictionary<string, object?> EmptyExtra = new();

    public object? this[string key]
    {
        get
        {
            switch (key)
            {
                case "type": return Type;
                case "start": return Start;
                case "end": return End;
                case "loc": return Loc;
                case "range": return Range;
                case "sourceFile": return SourceFile;
            }
            return _extra != null && _extra.TryGetValue(key, out var v) ? v : null;
        }
        set
        {
            switch (key)
            {
                case "type": Type = (string)value!; return;
                case "start": Start = Convert.ToInt32(value); return;
                case "end": End = Convert.ToInt32(value); return;
                case "loc": Loc = (SourceLocation?)value; return;
                case "range": Range = (int[]?)value; return;
                case "sourceFile": SourceFile = value; return;
                default:
                    (_extra ??= new Dictionary<string, object?>())[key] = value;
                    return;
            }
        }
    }

    public bool Has(string key) =>
        key is "type" or "start" or "end" or "loc" or "range" ||
        (_extra != null && _extra.ContainsKey(key));

    public void Remove(string key)
    {
        _extra?.Remove(key);
    }

    public bool TryGetProperty(string key, out object? value)
    {
        switch (key)
        {
            case "type": value = Type; return true;
            case "start": value = Start; return true;
            case "end": value = End; return true;
            case "loc": value = Loc; return true;
            case "range": value = Range; return true;
            case "sourceFile": value = SourceFile; return true;
        }
        if (_extra != null && _extra.TryGetValue(key, out value)) return true;
        value = null;
        return false;
    }
}

/// <summary>
/// Represents a regular-expression literal <c>value</c>. Acorn stores a real
/// JS <c>RegExp</c> here (or <c>null</c> if the host engine refuses it); we keep
/// the raw pattern/flags and render <c>ToString</c> as <c>/pattern/flags</c> to
/// match JavaScript's <c>RegExp.prototype.toString</c>.
/// </summary>
public sealed class RegExpLiteralValue
{
    public string Pattern;
    public string Flags;

    public RegExpLiteralValue(string pattern, string flags)
    {
        Pattern = pattern;
        Flags = flags;
    }

    public override string ToString() => "/" + Pattern + "/" + Flags;
}

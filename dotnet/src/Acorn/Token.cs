namespace Acorn;

/// <summary>
/// Represents a token. Normally tokens simply exist as properties on the
/// parser; this is used for the onToken callback and the external tokenizer.
/// </summary>
public sealed class Token : IPropertyBag
{
    public TokenType Type;
    public object? Value;
    public int Start;
    public int End;
    public SourceLocation? Loc;
    public int[]? Range;

    public Token(Parser p)
    {
        Type = p.Type;
        Value = p.Value;
        Start = p.Start;
        End = p.End;
        if (p.Options.Locations)
            Loc = new SourceLocation(p, p.StartLoc, p.EndLoc);
        if (p.Options.Ranges)
            Range = new[] { p.Start, p.End };
    }

    public bool TryGetProperty(string key, out object? value)
    {
        switch (key)
        {
            case "type": value = Type; return true;
            case "value": value = Value; return true;
            case "start": value = Start; return true;
            case "end": value = End; return true;
            case "loc": value = Loc; return true;
            case "range": value = Range; return true;
            default: value = null; return false;
        }
    }
}

/// <summary>The value carried by a regular-expression token.</summary>
public sealed class RegexpTokenValue : IPropertyBag
{
    public string Pattern;
    public string Flags;
    public object? Value;

    public RegexpTokenValue(string pattern, string flags, object? value)
    {
        Pattern = pattern;
        Flags = flags;
        Value = value;
    }

    public bool TryGetProperty(string key, out object? value)
    {
        switch (key)
        {
            case "pattern": value = Pattern; return true;
            case "flags": value = Flags; return true;
            case "value": value = Value; return true;
            default: value = null; return false;
        }
    }
}

/// <summary>Sentinel exception used to restart reading of an invalid template token.</summary>
internal sealed class InvalidTemplateEscapeException : Exception { }

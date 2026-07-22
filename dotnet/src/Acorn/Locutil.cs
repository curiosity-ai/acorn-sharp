namespace Acorn;

/// <summary>
/// A property bag abstraction used by the (test) tree comparison logic so that
/// nodes, source locations and positions can all be walked uniformly by name.
/// </summary>
public interface IPropertyBag
{
    bool TryGetProperty(string key, out object? value);
}

/// <summary>
/// A line/column position, used when <see cref="Options.Locations"/> is on for
/// the <c>startLoc</c> and <c>endLoc</c> properties.
/// </summary>
public sealed class Position : IPropertyBag
{
    public int Line;
    public int Column;

    public Position(int line, int col)
    {
        Line = line;
        Column = col;
    }

    public Position Offset(int n) => new Position(Line, Column + n);

    public bool TryGetProperty(string key, out object? value)
    {
        switch (key)
        {
            case "line": value = Line; return true;
            case "column": value = Column; return true;
            default: value = null; return false;
        }
    }
}

/// <summary>
/// The source location of a node: <c>start</c> and <c>end</c> positions, plus an
/// optional <c>source</c> file name.
/// </summary>
public sealed class SourceLocation : IPropertyBag
{
    public Position Start;
    public Position End;
    public object? Source;

    public SourceLocation(Parser p, Position? start, Position? end)
    {
        Start = start!;
        End = end!;
        if (p.SourceFile != null) Source = p.SourceFile;
    }

    public bool TryGetProperty(string key, out object? value)
    {
        switch (key)
        {
            case "start": value = Start; return true;
            case "end": value = End; return true;
            case "source": value = Source; return true;
            default: value = null; return false;
        }
    }
}

public static class LocUtil
{
    // The `getLineInfo` function is mostly useful when the `locations` option is
    // off and you want to find the line/column position for a given character offset.
    public static Position GetLineInfo(string input, int offset)
    {
        for (int line = 1, cur = 0; ;)
        {
            int nextBreak = Whitespace.NextLineBreak(input, cur, offset);
            if (nextBreak < 0) return new Position(line, offset - cur);
            ++line;
            cur = nextBreak;
        }
    }
}

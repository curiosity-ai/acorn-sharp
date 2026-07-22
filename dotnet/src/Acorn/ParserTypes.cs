namespace Acorn;

/// <summary>A SyntaxError raised by the parser.</summary>
public class AcornSyntaxError : Exception
{
    public int Pos;
    public Position? Loc;
    public int RaisedAt;

    public AcornSyntaxError(string message) : base(message) { }
}

/// <summary>A lexical/var scope, tracking declared names to detect duplicates.</summary>
public sealed class Scope
{
    public int Flags;
    public List<string> Var = new();
    public List<string> Lexical = new();
    public List<string> Functions = new();

    public Scope(int flags) { Flags = flags; }
}

/// <summary>An entry in the parser's label stack.</summary>
public sealed class LabelInfo
{
    public string? Name;
    public string? Kind;    // "loop" | "switch" | null
    public int StatementStart;
}

/// <summary>Tracks private names declared/used within a class body.</summary>
public sealed class PrivateNameStatus
{
    public Dictionary<string, string?> Declared = new();
    public List<Node> Used = new();
}

/// <summary>Accumulates recoverable destructuring/expression errors during parsing.</summary>
public sealed class DestructuringErrors
{
    public int ShorthandAssign = -1;
    public int TrailingComma = -1;
    public int ParenthesizedAssign = -1;
    public int ParenthesizedBind = -1;
    public int DoubleProto = -1;
}

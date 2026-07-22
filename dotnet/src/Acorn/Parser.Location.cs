namespace Acorn;

public partial class Parser
{
    // Raise a SyntaxError at the given offset (into the current input).
    public virtual void Raise(int pos, string message)
    {
        Position loc = LocUtil.GetLineInfo(Input, pos);
        message += " (" + loc.Line + ":" + loc.Column + ")";
        if (SourceFile != null) message += " in " + SourceFile;
        var err = new AcornSyntaxError(message) { Pos = pos, Loc = loc, RaisedAt = Pos };
        throw err;
    }

    public virtual void RaiseRecoverable(int pos, string message) => Raise(pos, message);

    public Position? CurPosition()
    {
        if (Options.Locations)
            return new Position(CurLine, Pos - LineStart);
        return null;
    }
}

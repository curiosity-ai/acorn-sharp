namespace Acorn;

public partial class Parser
{
    // Start an AST node, attaching a start offset.
    public Node StartNode() => new Node(this, Start, StartLoc);

    public Node StartNodeAt(int pos, Position? loc) => new Node(this, pos, loc);

    private Node DoFinishNodeAt(Node node, string type, int pos, Position? loc)
    {
        node.Type = type;
        node.End = pos;
        if (Options.Locations) node.Loc!.End = loc!;
        if (Options.Ranges) node.Range![1] = pos;
        return node;
    }

    // Finish an AST node, adding `type` and `end` properties.
    public Node FinishNode(Node node, string type) =>
        DoFinishNodeAt(node, type, LastTokEnd, LastTokEndLoc);

    // Finish node at given position.
    public Node FinishNodeAt(Node node, string type, int pos, Position? loc) =>
        DoFinishNodeAt(node, type, pos, loc);

    public Node CopyNode(Node node)
    {
        Node newNode = new Node(this, node.Start, StartLoc);
        newNode.Type = node.Type;
        newNode.End = node.End;
        if (node.Loc != null) newNode.Loc = node.Loc;
        if (node.Range != null) newNode.Range = node.Range;
        if (node.SourceFile != null) newNode.SourceFile = node.SourceFile;
        foreach (var kv in node.Extra) newNode[kv.Key] = kv.Value;
        return newNode;
    }
}

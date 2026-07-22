using Acorn;
using Acorn.Walk;
using Xunit;

namespace Acorn.Tests;

public class WalkTests
{
    private static Node Parse(string code) =>
        Parser.ParseStatic(code, new Options { EcmaVersion = 2020 });

    [Fact]
    public void SimpleVisitsMatchingNodes()
    {
        Node ast = Parse("f(1); g(2); h(3);");
        int calls = 0;
        AstWalker.Simple(ast, new Dictionary<string, Action<Node, object?>>
        {
            ["CallExpression"] = (node, state) => calls++
        });
        Assert.Equal(3, calls);
    }

    [Fact]
    public void FullVisitsEveryNode()
    {
        Node ast = Parse("var x = 1 + 2;");
        int count = 0;
        AstWalker.Full(ast, (node, state, type) => count++);
        // Program, VariableDeclaration, VariableDeclarator, Identifier(x),
        // BinaryExpression, Literal(1), Literal(2)
        Assert.True(count >= 6, $"expected many nodes, got {count}");
    }

    [Fact]
    public void FindNodeAtLocatesNode()
    {
        Node ast = Parse("a + b");
        var found = AstWalker.FindNodeAt(ast, 0, 1, "Identifier", null, null);
        Assert.NotNull(found);
        Assert.Equal("Identifier", found!.Node.Type);
        Assert.Equal("a", found.Node["name"]);
    }
}

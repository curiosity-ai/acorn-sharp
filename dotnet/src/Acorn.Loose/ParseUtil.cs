using Acorn;

namespace Acorn.Loose;

// Ported from acorn-loose/src/parseutil.js
public static class ParseUtil
{
    // The dummy identifier / literal name. Source code kept ASCII: this is U+2716.
    public const string DummyValue = "✖";

    public static bool IsDummy(Node? node) => node != null && (node["name"] as string) == DummyValue;
}

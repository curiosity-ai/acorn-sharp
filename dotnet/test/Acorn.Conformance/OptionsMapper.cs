using System.Text.Json;
using Acorn;

namespace Acorn.Conformance;

public static class OptionsMapper
{
    /// <summary>
    /// Builds an <see cref="Acorn.Options"/> from a fixture's options JSON,
    /// replicating the driver defaults (locations:true when no options given;
    /// ecmaVersion defaults to 5). Also extracts expected onComment/onToken
    /// arrays (the test author stores expected values there).
    /// </summary>
    public static Options Build(JsonElement? optsJson, out JsonElement? expectedComments,
        out JsonElement? expectedTokens, bool collectComments, bool collectTokens)
    {
        expectedComments = null;
        expectedTokens = null;
        var o = new Options();

        if (optsJson == null)
        {
            o.Locations = true;
            o.EcmaVersion = 5;
            return o;
        }

        var opts = optsJson.Value;
        foreach (var p in opts.EnumerateObject())
        {
            switch (p.Name)
            {
                case "ecmaVersion":
                    if (p.Value.ValueKind == JsonValueKind.String)
                        o.EcmaVersion = p.Value.GetString() == "latest" ? Options.Latest : 0;
                    else if (p.Value.ValueKind == JsonValueKind.Number)
                        o.EcmaVersion = p.Value.GetInt32();
                    break;
                case "sourceType": o.SourceType = p.Value.GetString() ?? "script"; break;
                case "strict": o.Strict = p.Value.ValueKind == JsonValueKind.True; break;
                case "locations": o.Locations = p.Value.ValueKind == JsonValueKind.True; break;
                case "ranges": o.Ranges = p.Value.ValueKind == JsonValueKind.True; break;
                case "preserveParens": o.PreserveParens = p.Value.ValueKind == JsonValueKind.True; break;
                case "allowReturnOutsideFunction": o.AllowReturnOutsideFunction = p.Value.ValueKind == JsonValueKind.True; break;
                case "allowImportExportEverywhere": o.AllowImportExportEverywhere = p.Value.ValueKind == JsonValueKind.True; break;
                case "allowAwaitOutsideFunction": o.AllowAwaitOutsideFunction = p.Value.ValueKind == JsonValueKind.True; break;
                case "allowSuperOutsideMethod": o.AllowSuperOutsideMethod = p.Value.ValueKind == JsonValueKind.True; break;
                case "checkPrivateFields": o.CheckPrivateFields = p.Value.ValueKind != JsonValueKind.False; break;
                case "allowHashBang": o.AllowHashBang = p.Value.ValueKind == JsonValueKind.True; break;
                case "allowReserved":
                    o.AllowReserved = p.Value.ValueKind switch
                    {
                        JsonValueKind.True => AllowReservedOption.True,
                        JsonValueKind.False => AllowReservedOption.False,
                        JsonValueKind.String when p.Value.GetString() == "never" => AllowReservedOption.Never,
                        _ => AllowReservedOption.Unspecified
                    };
                    break;
                case "sourceFile": o.SourceFile = p.Value.GetString(); break;
                case "directSourceFile": o.DirectSourceFile = p.Value.GetString(); break;
                case "onComment":
                    if (p.Value.ValueKind == JsonValueKind.Array) expectedComments = p.Value;
                    break;
                case "onToken":
                    if (p.Value.ValueKind == JsonValueKind.Array) expectedTokens = p.Value;
                    break;
            }
        }

        // driver: if (!testOpts.ecmaVersion) testOpts.ecmaVersion = 5
        if (o.EcmaVersion == 0) o.EcmaVersion = 5;

        if (collectComments && expectedComments != null) o.OnCommentList = new List<Comment>();
        if (collectTokens && expectedTokens != null) o.OnTokenList = new List<Token>();

        return o;
    }
}

namespace Acorn;

public enum AllowReservedOption { Unspecified, True, False, Never }

/// <summary>Callback invoked when a comment is skipped.</summary>
public delegate void OnCommentHandler(bool block, string text, int start, int end, Position? startLoc, Position? endLoc);

/// <summary>
/// Parser options. Mirrors acorn's <c>defaultOptions</c>/<c>getOptions</c>.
/// </summary>
public sealed class Options
{
    /// <summary>Sentinel meaning "the latest ECMAScript version the library supports".</summary>
    public const int Latest = 100000000;

    // ecmaVersion: 3, 5, 6/2015, 7/2016 ... or Options.Latest. 0 = unspecified.
    public int EcmaVersion = 0;
    public string SourceType = "script";
    public bool Strict = false;
    public Action<int, Position?>? OnInsertedSemicolon = null;
    public Action<int, Position?>? OnTrailingComma = null;
    public AllowReservedOption AllowReserved = AllowReservedOption.Unspecified;
    public bool AllowReturnOutsideFunction = false;
    public bool AllowImportExportEverywhere = false;
    public bool? AllowAwaitOutsideFunction = null;
    public bool? AllowSuperOutsideMethod = null;
    public bool? AllowHashBang = null;
    public bool CheckPrivateFields = true;
    public bool Locations = false;
    public Action<Token>? OnToken = null;
    public List<Token>? OnTokenList = null;
    public OnCommentHandler? OnComment = null;
    public List<Comment>? OnCommentList = null;
    public bool Ranges = false;
    public Node? Program = null;
    public string? SourceFile = null;
    public string? DirectSourceFile = null;
    public bool PreserveParens = false;

    // acorn-loose adds this to defaultOptions (defaultOptions.tabSize = 4).
    public int TabSize = 4;

    public Options Clone() => (Options)MemberwiseClone();

    /// <summary>Interpret and default an options object.</summary>
    public static Options GetOptions(Options? opts)
    {
        Options options = opts != null ? opts.Clone() : new Options();

        if (options.EcmaVersion == Latest)
        {
            // keep
        }
        else if (options.EcmaVersion == 0)
        {
            options.EcmaVersion = 11;
        }
        else if (options.EcmaVersion >= 2015)
        {
            options.EcmaVersion -= 2009;
        }

        if (options.AllowReserved == AllowReservedOption.Unspecified)
            options.AllowReserved = options.EcmaVersion < 5 ? AllowReservedOption.True : AllowReservedOption.False;

        if (opts == null || opts.AllowHashBang == null)
            options.AllowHashBang = options.EcmaVersion >= 14;

        if (options.SourceType == "commonjs" && options.AllowAwaitOutsideFunction == true)
            throw new Exception("Cannot use allowAwaitOutsideFunction with sourceType: commonjs");

        return options;
    }
}

/// <summary>Represents a skipped comment (for the onComment collector).</summary>
public sealed class Comment : IPropertyBag
{
    public string Type = "";
    public string Value = "";
    public int Start;
    public int End;
    public SourceLocation? Loc;
    public int[]? Range;

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

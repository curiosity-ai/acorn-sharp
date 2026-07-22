namespace Acorn;

/// <summary>Bit flags describing a lexical/var scope.</summary>
public static class ScopeFlags
{
    public const int SCOPE_TOP = 1;
    public const int SCOPE_FUNCTION = 2;
    public const int SCOPE_ASYNC = 4;
    public const int SCOPE_GENERATOR = 8;
    public const int SCOPE_ARROW = 16;
    public const int SCOPE_SIMPLE_CATCH = 32;
    public const int SCOPE_SUPER = 64;
    public const int SCOPE_DIRECT_SUPER = 128;
    public const int SCOPE_CLASS_STATIC_BLOCK = 256;
    public const int SCOPE_CLASS_FIELD_INIT = 512;
    public const int SCOPE_SWITCH = 1024;
    public const int SCOPE_VAR = SCOPE_TOP | SCOPE_FUNCTION | SCOPE_CLASS_STATIC_BLOCK;

    public static int FunctionFlags(bool async, bool generator) =>
        SCOPE_FUNCTION | (async ? SCOPE_ASYNC : 0) | (generator ? SCOPE_GENERATOR : 0);
}

/// <summary>Binding kinds, used in checkLVal* and declareName.</summary>
public static class BindFlags
{
    public const int BIND_NONE = 0;         // Not a binding
    public const int BIND_VAR = 1;          // Var-style binding
    public const int BIND_LEXICAL = 2;      // Let- or const-style binding
    public const int BIND_FUNCTION = 3;     // Function declaration
    public const int BIND_SIMPLE_CATCH = 4; // Simple (identifier pattern) catch binding
    public const int BIND_OUTSIDE = 5;      // Function names bound inside the function
}

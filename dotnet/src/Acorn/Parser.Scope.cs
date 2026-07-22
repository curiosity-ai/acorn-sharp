using static Acorn.ScopeFlags;
using static Acorn.BindFlags;

namespace Acorn;

public partial class Parser
{
    public void EnterScope(int flags) => ScopeStack.Add(new Scope(flags));

    public void ExitScope() => ScopeStack.RemoveAt(ScopeStack.Count - 1);

    // At the top level of a function or script, function declarations are
    // treated like var declarations rather than lexical declarations.
    public bool TreatFunctionsAsVarInScope(Scope scope) =>
        (scope.Flags & SCOPE_FUNCTION) != 0 || (!InModule && (scope.Flags & SCOPE_TOP) != 0);

    public void DeclareName(string name, int bindingType, int pos)
    {
        bool redeclared = false;
        if (bindingType == BIND_LEXICAL)
        {
            Scope scope = CurrentScope();
            redeclared = scope.Lexical.IndexOf(name) > -1 || scope.Functions.IndexOf(name) > -1 || scope.Var.IndexOf(name) > -1;
            scope.Lexical.Add(name);
            if (InModule && (scope.Flags & SCOPE_TOP) != 0)
                UndefinedExports.Remove(name);
        }
        else if (bindingType == BIND_SIMPLE_CATCH)
        {
            Scope scope = CurrentScope();
            scope.Lexical.Add(name);
        }
        else if (bindingType == BIND_FUNCTION)
        {
            Scope scope = CurrentScope();
            if (TreatFunctionsAsVar)
                redeclared = scope.Lexical.IndexOf(name) > -1;
            else
                redeclared = scope.Lexical.IndexOf(name) > -1 || scope.Var.IndexOf(name) > -1;
            scope.Functions.Add(name);
        }
        else
        {
            for (int i = ScopeStack.Count - 1; i >= 0; --i)
            {
                Scope scope = ScopeStack[i];
                if (scope.Lexical.IndexOf(name) > -1 && !((scope.Flags & SCOPE_SIMPLE_CATCH) != 0 && scope.Lexical[0] == name) ||
                    !TreatFunctionsAsVarInScope(scope) && scope.Functions.IndexOf(name) > -1)
                {
                    redeclared = true;
                    break;
                }
                scope.Var.Add(name);
                if (InModule && (scope.Flags & SCOPE_TOP) != 0)
                    UndefinedExports.Remove(name);
                if ((scope.Flags & SCOPE_VAR) != 0) break;
            }
        }
        if (redeclared) RaiseRecoverable(pos, $"Identifier '{name}' has already been declared");
    }

    public void CheckLocalExport(Node id)
    {
        // String-literal export names have no `name`; JS would index the map with
        // `undefined` and then immediately raise the literal-export error, so a
        // missing name here is a no-op.
        if (id["name"] is not string name) return;
        // scope.functions must be empty as Module code is always strict.
        if (ScopeStack[0].Lexical.IndexOf(name) == -1 &&
            ScopeStack[0].Var.IndexOf(name) == -1)
        {
            UndefinedExports[name] = id;
        }
    }

    public Scope CurrentScope() => ScopeStack[ScopeStack.Count - 1];

    public Scope CurrentVarScope()
    {
        for (int i = ScopeStack.Count - 1; ; i--)
        {
            Scope scope = ScopeStack[i];
            if ((scope.Flags & (SCOPE_VAR | SCOPE_CLASS_FIELD_INIT | SCOPE_CLASS_STATIC_BLOCK)) != 0) return scope;
        }
    }

    // Could be useful for `this`, `new.target`, `super()`, `super.property`, and `super[property]`.
    public Scope CurrentThisScope()
    {
        for (int i = ScopeStack.Count - 1; ; i--)
        {
            Scope scope = ScopeStack[i];
            if ((scope.Flags & (SCOPE_VAR | SCOPE_CLASS_FIELD_INIT | SCOPE_CLASS_STATIC_BLOCK)) != 0 &&
                (scope.Flags & SCOPE_ARROW) == 0) return scope;
        }
    }
}

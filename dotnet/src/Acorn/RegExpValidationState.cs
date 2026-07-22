namespace Acorn;

// Track disjunction structure to determine whether a duplicate
// capture group name is allowed because it is in a separate branch.
internal sealed class BranchID
{
    // Parent disjunction branch
    public BranchID? Parent;
    // Identifies this set of sibling branches
    public BranchID Base;

    public BranchID(BranchID? parent, BranchID? baseId)
    {
        Parent = parent;
        Base = baseId ?? this;
    }

    public bool SeparatedFrom(BranchID? alt)
    {
        // A branch is separate from another branch if they or any of
        // their parents are siblings in a given disjunction
        for (BranchID? self = this; self != null; self = self.Parent)
        {
            for (BranchID? other = alt; other != null; other = other.Parent)
            {
                if (ReferenceEquals(self.Base, other.Base) && !ReferenceEquals(self, other)) return true;
            }
        }
        return false;
    }

    public BranchID Sibling()
    {
        return new BranchID(Parent, Base);
    }
}

public sealed class RegExpValidationState
{
    public Parser Parser;
    public string ValidFlags;
    public UnicodePropertyValues? UnicodeProperties;
    public string Source;
    public string Flags;
    public int Start;
    public bool SwitchU;
    public bool SwitchV;
    public bool SwitchN;
    public int Pos;
    public int LastIntValue;
    public string LastStringValue;
    public bool LastAssertionIsQuantifiable;
    public int NumCapturingParens;
    public int MaxBackReference;
    // Object used as a set. In the non-tracked case values are the boxed bool
    // `true`; when disjunction tracking is enabled values are `List<BranchID>`.
    public Dictionary<string, object?> GroupNames;
    public List<string> BackReferenceNames;
    internal BranchID? BranchID;

    public RegExpValidationState(Parser parser)
    {
        Parser = parser;
        int ecmaVersion = parser.Options.EcmaVersion;
        ValidFlags = $"gim{(ecmaVersion >= 6 ? "uy" : "")}{(ecmaVersion >= 9 ? "s" : "")}{(ecmaVersion >= 13 ? "d" : "")}{(ecmaVersion >= 15 ? "v" : "")}";
        UnicodeProperties = UnicodePropertyData.Data.TryGetValue(ecmaVersion >= 14 ? 14 : ecmaVersion, out var up) ? up : null;
        Source = "";
        Flags = "";
        Start = 0;
        SwitchU = false;
        SwitchV = false;
        SwitchN = false;
        Pos = 0;
        LastIntValue = 0;
        LastStringValue = "";
        LastAssertionIsQuantifiable = false;
        NumCapturingParens = 0;
        MaxBackReference = 0;
        GroupNames = new Dictionary<string, object?>();
        BackReferenceNames = new List<string>();
        BranchID = null;
    }

    public void Reset(int start, string pattern, string flags)
    {
        bool unicodeSets = flags.IndexOf('v') != -1;
        bool unicode = flags.IndexOf('u') != -1;
        Start = start;
        Source = pattern + "";
        Flags = flags;
        if (unicodeSets && Parser.Options.EcmaVersion >= 15)
        {
            SwitchU = true;
            SwitchV = true;
            SwitchN = true;
        }
        else
        {
            SwitchU = unicode && Parser.Options.EcmaVersion >= 6;
            SwitchV = false;
            SwitchN = unicode && Parser.Options.EcmaVersion >= 9;
        }
    }

    public void Raise(string message)
    {
        Parser.RaiseRecoverable(Start, $"Invalid regular expression: /{Source}/: {message}");
    }

    // If u flag is given, this returns the code point at the index (it combines a surrogate pair).
    // Otherwise, this returns the code unit of the index (can be a part of a surrogate pair).
    public int At(int i, bool forceU = false)
    {
        string s = Source;
        int l = s.Length;
        if (i >= l)
        {
            return -1;
        }
        int c = s[i];
        if (!(forceU || SwitchU) || c <= 0xD7FF || c >= 0xE000 || i + 1 >= l)
        {
            return c;
        }
        int next = s[i + 1];
        return next >= 0xDC00 && next <= 0xDFFF ? (c << 10) + next - 0x35FDC00 : c;
    }

    public int NextIndex(int i, bool forceU = false)
    {
        string s = Source;
        int l = s.Length;
        if (i >= l)
        {
            return l;
        }
        int c = s[i], next;
        if (!(forceU || SwitchU) || c <= 0xD7FF || c >= 0xE000 || i + 1 >= l ||
            (next = s[i + 1]) < 0xDC00 || next > 0xDFFF)
        {
            return i + 1;
        }
        return i + 2;
    }

    public int Current(bool forceU = false)
    {
        return At(Pos, forceU);
    }

    public int Lookahead(bool forceU = false)
    {
        return At(NextIndex(Pos, forceU), forceU);
    }

    public void Advance(bool forceU = false)
    {
        Pos = NextIndex(Pos, forceU);
    }

    public bool Eat(int ch, bool forceU = false)
    {
        if (Current(forceU) == ch)
        {
            Advance(forceU);
            return true;
        }
        return false;
    }

    public bool EatChars(int[] chs, bool forceU = false)
    {
        int pos = Pos;
        foreach (int ch in chs)
        {
            int current = At(pos, forceU);
            if (current == -1 || current != ch)
            {
                return false;
            }
            pos = NextIndex(pos, forceU);
        }
        Pos = pos;
        return true;
    }
}

namespace Acorn;

public partial class Parser
{
    /// <summary>
    /// Validate the flags part of a given RegExpLiteral.
    /// </summary>
    public void ValidateRegExpFlags(RegExpValidationState state)
    {
        string validFlags = state.ValidFlags;
        string flags = state.Flags;

        bool u = false;
        bool v = false;

        for (int i = 0; i < flags.Length; i++)
        {
            string flag = flags[i].ToString();
            if (validFlags.IndexOf(flag, StringComparison.Ordinal) == -1)
            {
                Raise(state.Start, "Invalid regular expression flag");
            }
            if (flags.IndexOf(flag, i + 1, StringComparison.Ordinal) > -1)
            {
                Raise(state.Start, "Duplicate regular expression flag");
            }
            if (flag == "u") u = true;
            if (flag == "v") v = true;
        }
        if (Options.EcmaVersion >= 15 && u && v)
        {
            Raise(state.Start, "Invalid regular expression flag");
        }
    }

    private static bool HasProp(Dictionary<string, object?> obj)
    {
        foreach (var _ in obj) return true;
        return false;
    }

    /// <summary>
    /// Validate the pattern part of a given RegExpLiteral.
    /// </summary>
    public void ValidateRegExpPattern(RegExpValidationState state)
    {
        RegexpPattern(state);

        // The goal symbol for the parse is |Pattern[~U, ~N]|. If the result of
        // parsing contains a |GroupName|, reparse with the goal symbol
        // |Pattern[~U, +N]| and use this result instead. Throw a *SyntaxError*
        // exception if _P_ did not conform to the grammar, if any elements of _P_
        // were not matched by the parse, or if any Early Error conditions exist.
        if (!state.SwitchN && Options.EcmaVersion >= 9 && HasProp(state.GroupNames))
        {
            state.SwitchN = true;
            RegexpPattern(state);
        }
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-Pattern
    public void RegexpPattern(RegExpValidationState state)
    {
        state.Pos = 0;
        state.LastIntValue = 0;
        state.LastStringValue = "";
        state.LastAssertionIsQuantifiable = false;
        state.NumCapturingParens = 0;
        state.MaxBackReference = 0;
        state.GroupNames = new Dictionary<string, object?>();
        state.BackReferenceNames.Clear();
        state.BranchID = null;

        RegexpDisjunction(state);

        if (state.Pos != state.Source.Length)
        {
            // Make the same messages as V8.
            if (state.Eat(0x29 /* ) */))
            {
                state.Raise("Unmatched ')'");
            }
            if (state.Eat(0x5D /* ] */) || state.Eat(0x7D /* } */))
            {
                state.Raise("Lone quantifier brackets");
            }
        }
        if (state.MaxBackReference > state.NumCapturingParens)
        {
            state.Raise("Invalid escape");
        }
        foreach (string name in state.BackReferenceNames)
        {
            if (!state.GroupNames.ContainsKey(name))
            {
                state.Raise("Invalid named capture referenced");
            }
        }
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-Disjunction
    public void RegexpDisjunction(RegExpValidationState state)
    {
        bool trackDisjunction = Options.EcmaVersion >= 16;
        if (trackDisjunction) state.BranchID = new BranchID(state.BranchID, null);
        RegexpAlternative(state);
        while (state.Eat(0x7C /* | */))
        {
            if (trackDisjunction) state.BranchID = state.BranchID!.Sibling();
            RegexpAlternative(state);
        }
        if (trackDisjunction) state.BranchID = state.BranchID!.Parent;

        // Make the same message as V8.
        if (RegexpEatQuantifier(state, true))
        {
            state.Raise("Nothing to repeat");
        }
        if (state.Eat(0x7B /* { */))
        {
            state.Raise("Lone quantifier brackets");
        }
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-Alternative
    public void RegexpAlternative(RegExpValidationState state)
    {
        while (state.Pos < state.Source.Length && RegexpEatTerm(state)) { }
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-Term
    public bool RegexpEatTerm(RegExpValidationState state)
    {
        if (RegexpEatAssertion(state))
        {
            // Handle `QuantifiableAssertion Quantifier` alternative.
            // `state.lastAssertionIsQuantifiable` is true if the last eaten Assertion
            // is a QuantifiableAssertion.
            if (state.LastAssertionIsQuantifiable && RegexpEatQuantifier(state))
            {
                // Make the same message as V8.
                if (state.SwitchU)
                {
                    state.Raise("Invalid quantifier");
                }
            }
            return true;
        }

        if (state.SwitchU ? RegexpEatAtom(state) : RegexpEatExtendedAtom(state))
        {
            RegexpEatQuantifier(state);
            return true;
        }

        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-Assertion
    public bool RegexpEatAssertion(RegExpValidationState state)
    {
        int start = state.Pos;
        state.LastAssertionIsQuantifiable = false;

        // ^, $
        if (state.Eat(0x5E /* ^ */) || state.Eat(0x24 /* $ */))
        {
            return true;
        }

        // \b \B
        if (state.Eat(0x5C /* \ */))
        {
            if (state.Eat(0x42 /* B */) || state.Eat(0x62 /* b */))
            {
                return true;
            }
            state.Pos = start;
        }

        // Lookahead / Lookbehind
        if (state.Eat(0x28 /* ( */) && state.Eat(0x3F /* ? */))
        {
            bool lookbehind = false;
            if (Options.EcmaVersion >= 9)
            {
                lookbehind = state.Eat(0x3C /* < */);
            }
            if (state.Eat(0x3D /* = */) || state.Eat(0x21 /* ! */))
            {
                RegexpDisjunction(state);
                if (!state.Eat(0x29 /* ) */))
                {
                    state.Raise("Unterminated group");
                }
                state.LastAssertionIsQuantifiable = !lookbehind;
                return true;
            }
        }

        state.Pos = start;
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-Quantifier
    public bool RegexpEatQuantifier(RegExpValidationState state, bool noError = false)
    {
        if (RegexpEatQuantifierPrefix(state, noError))
        {
            state.Eat(0x3F /* ? */);
            return true;
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-QuantifierPrefix
    public bool RegexpEatQuantifierPrefix(RegExpValidationState state, bool noError)
    {
        return (
            state.Eat(0x2A /* * */) ||
            state.Eat(0x2B /* + */) ||
            state.Eat(0x3F /* ? */) ||
            RegexpEatBracedQuantifier(state, noError)
        );
    }

    public bool RegexpEatBracedQuantifier(RegExpValidationState state, bool noError)
    {
        int start = state.Pos;
        if (state.Eat(0x7B /* { */))
        {
            int min = 0, max = -1;
            if (RegexpEatDecimalDigits(state))
            {
                min = state.LastIntValue;
                if (state.Eat(0x2C /* , */) && RegexpEatDecimalDigits(state))
                {
                    max = state.LastIntValue;
                }
                if (state.Eat(0x7D /* } */))
                {
                    // SyntaxError in https://www.ecma-international.org/ecma-262/8.0/#sec-term
                    if (max != -1 && max < min && !noError)
                    {
                        state.Raise("numbers out of order in {} quantifier");
                    }
                    return true;
                }
            }
            if (state.SwitchU && !noError)
            {
                state.Raise("Incomplete quantifier");
            }
            state.Pos = start;
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-Atom
    public bool RegexpEatAtom(RegExpValidationState state)
    {
        return (
            RegexpEatPatternCharacters(state) ||
            state.Eat(0x2E /* . */) ||
            RegexpEatReverseSolidusAtomEscape(state) ||
            RegexpEatCharacterClass(state) ||
            RegexpEatUncapturingGroup(state) ||
            RegexpEatCapturingGroup(state)
        );
    }

    public bool RegexpEatReverseSolidusAtomEscape(RegExpValidationState state)
    {
        int start = state.Pos;
        if (state.Eat(0x5C /* \ */))
        {
            if (RegexpEatAtomEscape(state))
            {
                return true;
            }
            state.Pos = start;
        }
        return false;
    }

    public bool RegexpEatUncapturingGroup(RegExpValidationState state)
    {
        int start = state.Pos;
        if (state.Eat(0x28 /* ( */))
        {
            if (state.Eat(0x3F /* ? */))
            {
                if (Options.EcmaVersion >= 16)
                {
                    string addModifiers = RegexpEatModifiers(state);
                    bool hasHyphen = state.Eat(0x2D /* - */);
                    if (addModifiers != "" || hasHyphen)
                    {
                        for (int i = 0; i < addModifiers.Length; i++)
                        {
                            string modifier = addModifiers[i].ToString();
                            if (addModifiers.IndexOf(modifier, i + 1, StringComparison.Ordinal) > -1)
                            {
                                state.Raise("Duplicate regular expression modifiers");
                            }
                        }
                        if (hasHyphen)
                        {
                            string removeModifiers = RegexpEatModifiers(state);
                            if (addModifiers == "" && removeModifiers == "" && state.Current() == 0x3A /* : */)
                            {
                                state.Raise("Invalid regular expression modifiers");
                            }
                            for (int i = 0; i < removeModifiers.Length; i++)
                            {
                                string modifier = removeModifiers[i].ToString();
                                if (
                                    removeModifiers.IndexOf(modifier, i + 1, StringComparison.Ordinal) > -1 ||
                                    addModifiers.IndexOf(modifier, StringComparison.Ordinal) > -1
                                )
                                {
                                    state.Raise("Duplicate regular expression modifiers");
                                }
                            }
                        }
                    }
                }
                if (state.Eat(0x3A /* : */))
                {
                    RegexpDisjunction(state);
                    if (state.Eat(0x29 /* ) */))
                    {
                        return true;
                    }
                    state.Raise("Unterminated group");
                }
            }
            state.Pos = start;
        }
        return false;
    }

    public bool RegexpEatCapturingGroup(RegExpValidationState state)
    {
        if (state.Eat(0x28 /* ( */))
        {
            if (Options.EcmaVersion >= 9)
            {
                RegexpGroupSpecifier(state);
            }
            else if (state.Current() == 0x3F /* ? */)
            {
                state.Raise("Invalid group");
            }
            RegexpDisjunction(state);
            if (state.Eat(0x29 /* ) */))
            {
                state.NumCapturingParens += 1;
                return true;
            }
            state.Raise("Unterminated group");
        }
        return false;
    }

    // RegularExpressionModifiers ::
    //   [empty]
    //   RegularExpressionModifiers RegularExpressionModifier
    public string RegexpEatModifiers(RegExpValidationState state)
    {
        string modifiers = "";
        int ch;
        while ((ch = state.Current()) != -1 && IsRegularExpressionModifier(ch))
        {
            modifiers += Util.CodePointToString(ch);
            state.Advance();
        }
        return modifiers;
    }

    // RegularExpressionModifier :: one of
    //   `i` `m` `s`
    private static bool IsRegularExpressionModifier(int ch)
    {
        return ch == 0x69 /* i */ || ch == 0x6d /* m */ || ch == 0x73 /* s */;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-ExtendedAtom
    public bool RegexpEatExtendedAtom(RegExpValidationState state)
    {
        return (
            state.Eat(0x2E /* . */) ||
            RegexpEatReverseSolidusAtomEscape(state) ||
            RegexpEatCharacterClass(state) ||
            RegexpEatUncapturingGroup(state) ||
            RegexpEatCapturingGroup(state) ||
            RegexpEatInvalidBracedQuantifier(state) ||
            RegexpEatExtendedPatternCharacter(state)
        );
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-InvalidBracedQuantifier
    public bool RegexpEatInvalidBracedQuantifier(RegExpValidationState state)
    {
        if (RegexpEatBracedQuantifier(state, true))
        {
            state.Raise("Nothing to repeat");
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-SyntaxCharacter
    public bool RegexpEatSyntaxCharacter(RegExpValidationState state)
    {
        int ch = state.Current();
        if (IsSyntaxCharacter(ch))
        {
            state.LastIntValue = ch;
            state.Advance();
            return true;
        }
        return false;
    }

    private static bool IsSyntaxCharacter(int ch)
    {
        return (
            ch == 0x24 /* $ */ ||
            ch >= 0x28 /* ( */ && ch <= 0x2B /* + */ ||
            ch == 0x2E /* . */ ||
            ch == 0x3F /* ? */ ||
            ch >= 0x5B /* [ */ && ch <= 0x5E /* ^ */ ||
            ch >= 0x7B /* { */ && ch <= 0x7D /* } */
        );
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-PatternCharacter
    // But eat eager.
    public bool RegexpEatPatternCharacters(RegExpValidationState state)
    {
        int start = state.Pos;
        int ch;
        while ((ch = state.Current()) != -1 && !IsSyntaxCharacter(ch))
        {
            state.Advance();
        }
        return state.Pos != start;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-ExtendedPatternCharacter
    public bool RegexpEatExtendedPatternCharacter(RegExpValidationState state)
    {
        int ch = state.Current();
        if (
            ch != -1 &&
            ch != 0x24 /* $ */ &&
            !(ch >= 0x28 /* ( */ && ch <= 0x2B /* + */) &&
            ch != 0x2E /* . */ &&
            ch != 0x3F /* ? */ &&
            ch != 0x5B /* [ */ &&
            ch != 0x5E /* ^ */ &&
            ch != 0x7C /* | */
        )
        {
            state.Advance();
            return true;
        }
        return false;
    }

    // GroupSpecifier ::
    //   [empty]
    //   `?` GroupName
    public void RegexpGroupSpecifier(RegExpValidationState state)
    {
        if (state.Eat(0x3F /* ? */))
        {
            if (!RegexpEatGroupName(state)) state.Raise("Invalid group");
            bool trackDisjunction = Options.EcmaVersion >= 16;
            object? known = state.GroupNames.TryGetValue(state.LastStringValue, out var v) ? v : null;
            if (known != null)
            {
                if (trackDisjunction)
                {
                    foreach (BranchID altID in (List<BranchID>)known)
                    {
                        if (!altID.SeparatedFrom(state.BranchID))
                            state.Raise("Duplicate capture group name");
                    }
                }
                else
                {
                    state.Raise("Duplicate capture group name");
                }
            }
            if (trackDisjunction)
            {
                List<BranchID> list;
                if (known != null)
                {
                    list = (List<BranchID>)known;
                }
                else
                {
                    list = new List<BranchID>();
                    state.GroupNames[state.LastStringValue] = list;
                }
                list.Add(state.BranchID!);
            }
            else
            {
                state.GroupNames[state.LastStringValue] = true;
            }
        }
    }

    // GroupName ::
    //   `<` RegExpIdentifierName `>`
    // Note: this updates `state.lastStringValue` property with the eaten name.
    public bool RegexpEatGroupName(RegExpValidationState state)
    {
        state.LastStringValue = "";
        if (state.Eat(0x3C /* < */))
        {
            if (RegexpEatRegExpIdentifierName(state) && state.Eat(0x3E /* > */))
            {
                return true;
            }
            state.Raise("Invalid capture group name");
        }
        return false;
    }

    // RegExpIdentifierName ::
    //   RegExpIdentifierStart
    //   RegExpIdentifierName RegExpIdentifierPart
    // Note: this updates `state.lastStringValue` property with the eaten name.
    public bool RegexpEatRegExpIdentifierName(RegExpValidationState state)
    {
        state.LastStringValue = "";
        if (RegexpEatRegExpIdentifierStart(state))
        {
            state.LastStringValue += Util.CodePointToString(state.LastIntValue);
            while (RegexpEatRegExpIdentifierPart(state))
            {
                state.LastStringValue += Util.CodePointToString(state.LastIntValue);
            }
            return true;
        }
        return false;
    }

    // RegExpIdentifierStart ::
    //   UnicodeIDStart
    //   `$`
    //   `_`
    //   `\` RegExpUnicodeEscapeSequence[+U]
    public bool RegexpEatRegExpIdentifierStart(RegExpValidationState state)
    {
        int start = state.Pos;
        bool forceU = Options.EcmaVersion >= 11;
        int ch = state.Current(forceU);
        state.Advance(forceU);

        if (ch == 0x5C /* \ */ && RegexpEatRegExpUnicodeEscapeSequence(state, forceU))
        {
            ch = state.LastIntValue;
        }
        if (IsRegExpIdentifierStart(ch))
        {
            state.LastIntValue = ch;
            return true;
        }

        state.Pos = start;
        return false;
    }

    private static bool IsRegExpIdentifierStart(int ch)
    {
        return Identifier.IsIdentifierStart(ch, true) || ch == 0x24 /* $ */ || ch == 0x5F /* _ */;
    }

    // RegExpIdentifierPart ::
    //   UnicodeIDContinue
    //   `$`
    //   `_`
    //   `\` RegExpUnicodeEscapeSequence[+U]
    //   <ZWNJ>
    //   <ZWJ>
    public bool RegexpEatRegExpIdentifierPart(RegExpValidationState state)
    {
        int start = state.Pos;
        bool forceU = Options.EcmaVersion >= 11;
        int ch = state.Current(forceU);
        state.Advance(forceU);

        if (ch == 0x5C /* \ */ && RegexpEatRegExpUnicodeEscapeSequence(state, forceU))
        {
            ch = state.LastIntValue;
        }
        if (IsRegExpIdentifierPart(ch))
        {
            state.LastIntValue = ch;
            return true;
        }

        state.Pos = start;
        return false;
    }

    private static bool IsRegExpIdentifierPart(int ch)
    {
        return Identifier.IsIdentifierChar(ch, true) || ch == 0x24 /* $ */ || ch == 0x5F /* _ */ || ch == 0x200C /* <ZWNJ> */ || ch == 0x200D /* <ZWJ> */;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-AtomEscape
    public bool RegexpEatAtomEscape(RegExpValidationState state)
    {
        if (
            RegexpEatBackReference(state) ||
            RegexpEatCharacterClassEscape(state) != CharSetNone ||
            RegexpEatCharacterEscape(state) ||
            (state.SwitchN && RegexpEatKGroupName(state))
        )
        {
            return true;
        }
        if (state.SwitchU)
        {
            // Make the same message as V8.
            if (state.Current() == 0x63 /* c */)
            {
                state.Raise("Invalid unicode escape");
            }
            state.Raise("Invalid escape");
        }
        return false;
    }

    public bool RegexpEatBackReference(RegExpValidationState state)
    {
        int start = state.Pos;
        if (RegexpEatDecimalEscape(state))
        {
            int n = state.LastIntValue;
            if (state.SwitchU)
            {
                // For SyntaxError in https://www.ecma-international.org/ecma-262/8.0/#sec-atomescape
                if (n > state.MaxBackReference)
                {
                    state.MaxBackReference = n;
                }
                return true;
            }
            if (n <= state.NumCapturingParens)
            {
                return true;
            }
            state.Pos = start;
        }
        return false;
    }

    public bool RegexpEatKGroupName(RegExpValidationState state)
    {
        if (state.Eat(0x6B /* k */))
        {
            if (RegexpEatGroupName(state))
            {
                state.BackReferenceNames.Add(state.LastStringValue);
                return true;
            }
            state.Raise("Invalid named reference");
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-CharacterEscape
    public bool RegexpEatCharacterEscape(RegExpValidationState state)
    {
        return (
            RegexpEatControlEscape(state) ||
            RegexpEatCControlLetter(state) ||
            RegexpEatZero(state) ||
            RegexpEatHexEscapeSequence(state) ||
            RegexpEatRegExpUnicodeEscapeSequence(state, false) ||
            (!state.SwitchU && RegexpEatLegacyOctalEscapeSequence(state)) ||
            RegexpEatIdentityEscape(state)
        );
    }

    public bool RegexpEatCControlLetter(RegExpValidationState state)
    {
        int start = state.Pos;
        if (state.Eat(0x63 /* c */))
        {
            if (RegexpEatControlLetter(state))
            {
                return true;
            }
            state.Pos = start;
        }
        return false;
    }

    public bool RegexpEatZero(RegExpValidationState state)
    {
        if (state.Current() == 0x30 /* 0 */ && !IsDecimalDigit(state.Lookahead()))
        {
            state.LastIntValue = 0;
            state.Advance();
            return true;
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-ControlEscape
    public bool RegexpEatControlEscape(RegExpValidationState state)
    {
        int ch = state.Current();
        if (ch == 0x74 /* t */)
        {
            state.LastIntValue = 0x09 /* \t */;
            state.Advance();
            return true;
        }
        if (ch == 0x6E /* n */)
        {
            state.LastIntValue = 0x0A /* \n */;
            state.Advance();
            return true;
        }
        if (ch == 0x76 /* v */)
        {
            state.LastIntValue = 0x0B /* \v */;
            state.Advance();
            return true;
        }
        if (ch == 0x66 /* f */)
        {
            state.LastIntValue = 0x0C /* \f */;
            state.Advance();
            return true;
        }
        if (ch == 0x72 /* r */)
        {
            state.LastIntValue = 0x0D /* \r */;
            state.Advance();
            return true;
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-ControlLetter
    public bool RegexpEatControlLetter(RegExpValidationState state)
    {
        int ch = state.Current();
        if (IsControlLetter(ch))
        {
            state.LastIntValue = ch % 0x20;
            state.Advance();
            return true;
        }
        return false;
    }

    private static bool IsControlLetter(int ch)
    {
        return (
            (ch >= 0x41 /* A */ && ch <= 0x5A /* Z */) ||
            (ch >= 0x61 /* a */ && ch <= 0x7A /* z */)
        );
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-RegExpUnicodeEscapeSequence
    public bool RegexpEatRegExpUnicodeEscapeSequence(RegExpValidationState state, bool forceU = false)
    {
        int start = state.Pos;
        bool switchU = forceU || state.SwitchU;

        if (state.Eat(0x75 /* u */))
        {
            if (RegexpEatFixedHexDigits(state, 4))
            {
                int lead = state.LastIntValue;
                if (switchU && lead >= 0xD800 && lead <= 0xDBFF)
                {
                    int leadSurrogateEnd = state.Pos;
                    if (state.Eat(0x5C /* \ */) && state.Eat(0x75 /* u */) && RegexpEatFixedHexDigits(state, 4))
                    {
                        int trail = state.LastIntValue;
                        if (trail >= 0xDC00 && trail <= 0xDFFF)
                        {
                            state.LastIntValue = (lead - 0xD800) * 0x400 + (trail - 0xDC00) + 0x10000;
                            return true;
                        }
                    }
                    state.Pos = leadSurrogateEnd;
                    state.LastIntValue = lead;
                }
                return true;
            }
            if (
                switchU &&
                state.Eat(0x7B /* { */) &&
                RegexpEatHexDigits(state) &&
                state.Eat(0x7D /* } */) &&
                IsValidUnicode(state.LastIntValue)
            )
            {
                return true;
            }
            if (switchU)
            {
                state.Raise("Invalid unicode escape");
            }
            state.Pos = start;
        }

        return false;
    }

    private static bool IsValidUnicode(int ch)
    {
        return ch >= 0 && ch <= 0x10FFFF;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-IdentityEscape
    public bool RegexpEatIdentityEscape(RegExpValidationState state)
    {
        if (state.SwitchU)
        {
            if (RegexpEatSyntaxCharacter(state))
            {
                return true;
            }
            if (state.Eat(0x2F /* / */))
            {
                state.LastIntValue = 0x2F /* / */;
                return true;
            }
            return false;
        }

        int ch = state.Current();
        if (ch != 0x63 /* c */ && (!state.SwitchN || ch != 0x6B /* k */))
        {
            state.LastIntValue = ch;
            state.Advance();
            return true;
        }

        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-DecimalEscape
    public bool RegexpEatDecimalEscape(RegExpValidationState state)
    {
        state.LastIntValue = 0;
        int ch = state.Current();
        if (ch >= 0x31 /* 1 */ && ch <= 0x39 /* 9 */)
        {
            do
            {
                state.LastIntValue = 10 * state.LastIntValue + (ch - 0x30 /* 0 */);
                state.Advance();
            } while ((ch = state.Current()) >= 0x30 /* 0 */ && ch <= 0x39 /* 9 */);
            return true;
        }
        return false;
    }

    // Return values used by character set parsing methods, needed to
    // forbid negation of sets that can match strings.
    private const int CharSetNone = 0; // Nothing parsed
    private const int CharSetOk = 1; // Construct parsed, cannot contain strings
    private const int CharSetString = 2; // Construct parsed, can contain strings

    // https://www.ecma-international.org/ecma-262/8.0/#prod-CharacterClassEscape
    public int RegexpEatCharacterClassEscape(RegExpValidationState state)
    {
        int ch = state.Current();

        if (IsCharacterClassEscape(ch))
        {
            state.LastIntValue = -1;
            state.Advance();
            return CharSetOk;
        }

        bool negate = false;
        if (
            state.SwitchU &&
            Options.EcmaVersion >= 9 &&
            ((negate = ch == 0x50 /* P */) || ch == 0x70 /* p */)
        )
        {
            state.LastIntValue = -1;
            state.Advance();
            int result = CharSetNone;
            if (
                state.Eat(0x7B /* { */) &&
                (result = RegexpEatUnicodePropertyValueExpression(state)) != CharSetNone &&
                state.Eat(0x7D /* } */)
            )
            {
                if (negate && result == CharSetString) state.Raise("Invalid property name");
                return result;
            }
            state.Raise("Invalid property name");
        }

        return CharSetNone;
    }

    private static bool IsCharacterClassEscape(int ch)
    {
        return (
            ch == 0x64 /* d */ ||
            ch == 0x44 /* D */ ||
            ch == 0x73 /* s */ ||
            ch == 0x53 /* S */ ||
            ch == 0x77 /* w */ ||
            ch == 0x57 /* W */
        );
    }

    // UnicodePropertyValueExpression ::
    //   UnicodePropertyName `=` UnicodePropertyValue
    //   LoneUnicodePropertyNameOrValue
    public int RegexpEatUnicodePropertyValueExpression(RegExpValidationState state)
    {
        int start = state.Pos;

        // UnicodePropertyName `=` UnicodePropertyValue
        if (RegexpEatUnicodePropertyName(state) && state.Eat(0x3D /* = */))
        {
            string name = state.LastStringValue;
            if (RegexpEatUnicodePropertyValue(state))
            {
                string value = state.LastStringValue;
                RegexpValidateUnicodePropertyNameAndValue(state, name, value);
                return CharSetOk;
            }
        }
        state.Pos = start;

        // LoneUnicodePropertyNameOrValue
        if (RegexpEatLoneUnicodePropertyNameOrValue(state))
        {
            string nameOrValue = state.LastStringValue;
            return RegexpValidateUnicodePropertyNameOrValue(state, nameOrValue);
        }
        return CharSetNone;
    }

    public void RegexpValidateUnicodePropertyNameAndValue(RegExpValidationState state, string name, string value)
    {
        if (!state.UnicodeProperties!.NonBinary.ContainsKey(name))
            state.Raise("Invalid property name");
        if (!state.UnicodeProperties.NonBinary[name].IsMatch(value))
            state.Raise("Invalid property value");
    }

    public int RegexpValidateUnicodePropertyNameOrValue(RegExpValidationState state, string nameOrValue)
    {
        if (state.UnicodeProperties!.Binary.IsMatch(nameOrValue)) return CharSetOk;
        if (state.SwitchV && state.UnicodeProperties.BinaryOfStrings.IsMatch(nameOrValue)) return CharSetString;
        state.Raise("Invalid property name");
        return CharSetNone;
    }

    // UnicodePropertyName ::
    //   UnicodePropertyNameCharacters
    public bool RegexpEatUnicodePropertyName(RegExpValidationState state)
    {
        int ch;
        state.LastStringValue = "";
        while (IsUnicodePropertyNameCharacter(ch = state.Current()))
        {
            state.LastStringValue += Util.CodePointToString(ch);
            state.Advance();
        }
        return state.LastStringValue != "";
    }

    private static bool IsUnicodePropertyNameCharacter(int ch)
    {
        return IsControlLetter(ch) || ch == 0x5F /* _ */;
    }

    // UnicodePropertyValue ::
    //   UnicodePropertyValueCharacters
    public bool RegexpEatUnicodePropertyValue(RegExpValidationState state)
    {
        int ch;
        state.LastStringValue = "";
        while (IsUnicodePropertyValueCharacter(ch = state.Current()))
        {
            state.LastStringValue += Util.CodePointToString(ch);
            state.Advance();
        }
        return state.LastStringValue != "";
    }

    private static bool IsUnicodePropertyValueCharacter(int ch)
    {
        return IsUnicodePropertyNameCharacter(ch) || IsDecimalDigit(ch);
    }

    // LoneUnicodePropertyNameOrValue ::
    //   UnicodePropertyValueCharacters
    public bool RegexpEatLoneUnicodePropertyNameOrValue(RegExpValidationState state)
    {
        return RegexpEatUnicodePropertyValue(state);
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-CharacterClass
    public bool RegexpEatCharacterClass(RegExpValidationState state)
    {
        if (state.Eat(0x5B /* [ */))
        {
            bool negate = state.Eat(0x5E /* ^ */);
            int result = RegexpClassContents(state);
            if (!state.Eat(0x5D /* ] */))
                state.Raise("Unterminated character class");
            if (negate && result == CharSetString)
                state.Raise("Negated character class may contain strings");
            return true;
        }
        return false;
    }

    // https://tc39.es/ecma262/#prod-ClassContents
    // https://www.ecma-international.org/ecma-262/8.0/#prod-ClassRanges
    public int RegexpClassContents(RegExpValidationState state)
    {
        if (state.Current() == 0x5D /* ] */) return CharSetOk;
        if (state.SwitchV) return RegexpClassSetExpression(state);
        RegexpNonEmptyClassRanges(state);
        return CharSetOk;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-NonemptyClassRanges
    // https://www.ecma-international.org/ecma-262/8.0/#prod-NonemptyClassRangesNoDash
    public void RegexpNonEmptyClassRanges(RegExpValidationState state)
    {
        while (RegexpEatClassAtom(state))
        {
            int left = state.LastIntValue;
            if (state.Eat(0x2D /* - */) && RegexpEatClassAtom(state))
            {
                int right = state.LastIntValue;
                if (state.SwitchU && (left == -1 || right == -1))
                {
                    state.Raise("Invalid character class");
                }
                if (left != -1 && right != -1 && left > right)
                {
                    state.Raise("Range out of order in character class");
                }
            }
        }
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-ClassAtom
    // https://www.ecma-international.org/ecma-262/8.0/#prod-ClassAtomNoDash
    public bool RegexpEatClassAtom(RegExpValidationState state)
    {
        int start = state.Pos;

        if (state.Eat(0x5C /* \ */))
        {
            if (RegexpEatClassEscape(state))
            {
                return true;
            }
            if (state.SwitchU)
            {
                // Make the same message as V8.
                int ch1 = state.Current();
                if (ch1 == 0x63 /* c */ || IsOctalDigit(ch1))
                {
                    state.Raise("Invalid class escape");
                }
                state.Raise("Invalid escape");
            }
            state.Pos = start;
        }

        int ch = state.Current();
        if (ch != 0x5D /* ] */)
        {
            state.LastIntValue = ch;
            state.Advance();
            return true;
        }

        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-ClassEscape
    public bool RegexpEatClassEscape(RegExpValidationState state)
    {
        int start = state.Pos;

        if (state.Eat(0x62 /* b */))
        {
            state.LastIntValue = 0x08 /* <BS> */;
            return true;
        }

        if (state.SwitchU && state.Eat(0x2D /* - */))
        {
            state.LastIntValue = 0x2D /* - */;
            return true;
        }

        if (!state.SwitchU && state.Eat(0x63 /* c */))
        {
            if (RegexpEatClassControlLetter(state))
            {
                return true;
            }
            state.Pos = start;
        }

        return (
            RegexpEatCharacterClassEscape(state) != CharSetNone ||
            RegexpEatCharacterEscape(state)
        );
    }

    // https://tc39.es/ecma262/#prod-ClassSetExpression
    // https://tc39.es/ecma262/#prod-ClassUnion
    // https://tc39.es/ecma262/#prod-ClassIntersection
    // https://tc39.es/ecma262/#prod-ClassSubtraction
    public int RegexpClassSetExpression(RegExpValidationState state)
    {
        int result = CharSetOk, subResult;
        if (RegexpEatClassSetRange(state))
        {
            // Continue with ClassUnion processing.
        }
        else if ((subResult = RegexpEatClassSetOperand(state)) != CharSetNone)
        {
            if (subResult == CharSetString) result = CharSetString;
            // https://tc39.es/ecma262/#prod-ClassIntersection
            int start = state.Pos;
            while (state.EatChars(new[] { 0x26, 0x26 } /* && */))
            {
                if (
                    state.Current() != 0x26 /* & */ &&
                    (subResult = RegexpEatClassSetOperand(state)) != CharSetNone
                )
                {
                    if (subResult != CharSetString) result = CharSetOk;
                    continue;
                }
                state.Raise("Invalid character in character class");
            }
            if (start != state.Pos) return result;
            // https://tc39.es/ecma262/#prod-ClassSubtraction
            while (state.EatChars(new[] { 0x2D, 0x2D } /* -- */))
            {
                if (RegexpEatClassSetOperand(state) != CharSetNone) continue;
                state.Raise("Invalid character in character class");
            }
            if (start != state.Pos) return result;
        }
        else
        {
            state.Raise("Invalid character in character class");
        }
        // https://tc39.es/ecma262/#prod-ClassUnion
        for (; ; )
        {
            if (RegexpEatClassSetRange(state)) continue;
            subResult = RegexpEatClassSetOperand(state);
            if (subResult == CharSetNone) return result;
            if (subResult == CharSetString) result = CharSetString;
        }
    }

    // https://tc39.es/ecma262/#prod-ClassSetRange
    public bool RegexpEatClassSetRange(RegExpValidationState state)
    {
        int start = state.Pos;
        if (RegexpEatClassSetCharacter(state))
        {
            int left = state.LastIntValue;
            if (state.Eat(0x2D /* - */) && RegexpEatClassSetCharacter(state))
            {
                int right = state.LastIntValue;
                if (left != -1 && right != -1 && left > right)
                {
                    state.Raise("Range out of order in character class");
                }
                return true;
            }
            state.Pos = start;
        }
        return false;
    }

    // https://tc39.es/ecma262/#prod-ClassSetOperand
    public int RegexpEatClassSetOperand(RegExpValidationState state)
    {
        if (RegexpEatClassSetCharacter(state)) return CharSetOk;
        int result = RegexpEatClassStringDisjunction(state);
        if (result != CharSetNone) return result;
        return RegexpEatNestedClass(state);
    }

    // https://tc39.es/ecma262/#prod-NestedClass
    public int RegexpEatNestedClass(RegExpValidationState state)
    {
        int start = state.Pos;
        if (state.Eat(0x5B /* [ */))
        {
            bool negate = state.Eat(0x5E /* ^ */);
            int result = RegexpClassContents(state);
            if (state.Eat(0x5D /* ] */))
            {
                if (negate && result == CharSetString)
                {
                    state.Raise("Negated character class may contain strings");
                }
                return result;
            }
            state.Pos = start;
        }
        if (state.Eat(0x5C /* \ */))
        {
            int result = RegexpEatCharacterClassEscape(state);
            if (result != CharSetNone)
            {
                return result;
            }
            state.Pos = start;
        }
        return CharSetNone;
    }

    // https://tc39.es/ecma262/#prod-ClassStringDisjunction
    public int RegexpEatClassStringDisjunction(RegExpValidationState state)
    {
        int start = state.Pos;
        if (state.EatChars(new[] { 0x5C, 0x71 } /* \q */))
        {
            if (state.Eat(0x7B /* { */))
            {
                int result = RegexpClassStringDisjunctionContents(state);
                if (state.Eat(0x7D /* } */))
                {
                    return result;
                }
            }
            else
            {
                // Make the same message as V8.
                state.Raise("Invalid escape");
            }
            state.Pos = start;
        }
        return CharSetNone;
    }

    // https://tc39.es/ecma262/#prod-ClassStringDisjunctionContents
    public int RegexpClassStringDisjunctionContents(RegExpValidationState state)
    {
        int result = RegexpClassString(state);
        while (state.Eat(0x7C /* | */))
        {
            if (RegexpClassString(state) == CharSetString) result = CharSetString;
        }
        return result;
    }

    // https://tc39.es/ecma262/#prod-ClassString
    // https://tc39.es/ecma262/#prod-NonEmptyClassString
    public int RegexpClassString(RegExpValidationState state)
    {
        int count = 0;
        while (RegexpEatClassSetCharacter(state)) count++;
        return count == 1 ? CharSetOk : CharSetString;
    }

    // https://tc39.es/ecma262/#prod-ClassSetCharacter
    public bool RegexpEatClassSetCharacter(RegExpValidationState state)
    {
        int start = state.Pos;
        if (state.Eat(0x5C /* \ */))
        {
            if (
                RegexpEatCharacterEscape(state) ||
                RegexpEatClassSetReservedPunctuator(state)
            )
            {
                return true;
            }
            if (state.Eat(0x62 /* b */))
            {
                state.LastIntValue = 0x08 /* <BS> */;
                return true;
            }
            state.Pos = start;
            return false;
        }
        int ch = state.Current();
        if (ch < 0 || ch == state.Lookahead() && IsClassSetReservedDoublePunctuatorCharacter(ch)) return false;
        if (IsClassSetSyntaxCharacter(ch)) return false;
        state.Advance();
        state.LastIntValue = ch;
        return true;
    }

    // https://tc39.es/ecma262/#prod-ClassSetReservedDoublePunctuator
    private static bool IsClassSetReservedDoublePunctuatorCharacter(int ch)
    {
        return (
            ch == 0x21 /* ! */ ||
            ch >= 0x23 /* # */ && ch <= 0x26 /* & */ ||
            ch >= 0x2A /* * */ && ch <= 0x2C /* , */ ||
            ch == 0x2E /* . */ ||
            ch >= 0x3A /* : */ && ch <= 0x40 /* @ */ ||
            ch == 0x5E /* ^ */ ||
            ch == 0x60 /* ` */ ||
            ch == 0x7E /* ~ */
        );
    }

    // https://tc39.es/ecma262/#prod-ClassSetSyntaxCharacter
    private static bool IsClassSetSyntaxCharacter(int ch)
    {
        return (
            ch == 0x28 /* ( */ ||
            ch == 0x29 /* ) */ ||
            ch == 0x2D /* - */ ||
            ch == 0x2F /* / */ ||
            ch >= 0x5B /* [ */ && ch <= 0x5D /* ] */ ||
            ch >= 0x7B /* { */ && ch <= 0x7D /* } */
        );
    }

    // https://tc39.es/ecma262/#prod-ClassSetReservedPunctuator
    public bool RegexpEatClassSetReservedPunctuator(RegExpValidationState state)
    {
        int ch = state.Current();
        if (IsClassSetReservedPunctuator(ch))
        {
            state.LastIntValue = ch;
            state.Advance();
            return true;
        }
        return false;
    }

    // https://tc39.es/ecma262/#prod-ClassSetReservedPunctuator
    private static bool IsClassSetReservedPunctuator(int ch)
    {
        return (
            ch == 0x21 /* ! */ ||
            ch == 0x23 /* # */ ||
            ch == 0x25 /* % */ ||
            ch == 0x26 /* & */ ||
            ch == 0x2C /* , */ ||
            ch == 0x2D /* - */ ||
            ch >= 0x3A /* : */ && ch <= 0x3E /* > */ ||
            ch == 0x40 /* @ */ ||
            ch == 0x60 /* ` */ ||
            ch == 0x7E /* ~ */
        );
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-ClassControlLetter
    public bool RegexpEatClassControlLetter(RegExpValidationState state)
    {
        int ch = state.Current();
        if (IsDecimalDigit(ch) || ch == 0x5F /* _ */)
        {
            state.LastIntValue = ch % 0x20;
            state.Advance();
            return true;
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-HexEscapeSequence
    public bool RegexpEatHexEscapeSequence(RegExpValidationState state)
    {
        int start = state.Pos;
        if (state.Eat(0x78 /* x */))
        {
            if (RegexpEatFixedHexDigits(state, 2))
            {
                return true;
            }
            if (state.SwitchU)
            {
                state.Raise("Invalid escape");
            }
            state.Pos = start;
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-DecimalDigits
    public bool RegexpEatDecimalDigits(RegExpValidationState state)
    {
        int start = state.Pos;
        int ch;
        state.LastIntValue = 0;
        while (IsDecimalDigit(ch = state.Current()))
        {
            state.LastIntValue = 10 * state.LastIntValue + (ch - 0x30 /* 0 */);
            state.Advance();
        }
        return state.Pos != start;
    }

    private static bool IsDecimalDigit(int ch)
    {
        return ch >= 0x30 /* 0 */ && ch <= 0x39 /* 9 */;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-HexDigits
    public bool RegexpEatHexDigits(RegExpValidationState state)
    {
        int start = state.Pos;
        int ch;
        state.LastIntValue = 0;
        while (IsHexDigit(ch = state.Current()))
        {
            state.LastIntValue = 16 * state.LastIntValue + HexToInt(ch);
            state.Advance();
        }
        return state.Pos != start;
    }

    private static bool IsHexDigit(int ch)
    {
        return (
            (ch >= 0x30 /* 0 */ && ch <= 0x39 /* 9 */) ||
            (ch >= 0x41 /* A */ && ch <= 0x46 /* F */) ||
            (ch >= 0x61 /* a */ && ch <= 0x66 /* f */)
        );
    }

    private static int HexToInt(int ch)
    {
        if (ch >= 0x41 /* A */ && ch <= 0x46 /* F */)
        {
            return 10 + (ch - 0x41 /* A */);
        }
        if (ch >= 0x61 /* a */ && ch <= 0x66 /* f */)
        {
            return 10 + (ch - 0x61 /* a */);
        }
        return ch - 0x30 /* 0 */;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-annexB-LegacyOctalEscapeSequence
    // Allows only 0-377(octal) i.e. 0-255(decimal).
    public bool RegexpEatLegacyOctalEscapeSequence(RegExpValidationState state)
    {
        if (RegexpEatOctalDigit(state))
        {
            int n1 = state.LastIntValue;
            if (RegexpEatOctalDigit(state))
            {
                int n2 = state.LastIntValue;
                if (n1 <= 3 && RegexpEatOctalDigit(state))
                {
                    state.LastIntValue = n1 * 64 + n2 * 8 + state.LastIntValue;
                }
                else
                {
                    state.LastIntValue = n1 * 8 + n2;
                }
            }
            else
            {
                state.LastIntValue = n1;
            }
            return true;
        }
        return false;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-OctalDigit
    public bool RegexpEatOctalDigit(RegExpValidationState state)
    {
        int ch = state.Current();
        if (IsOctalDigit(ch))
        {
            state.LastIntValue = ch - 0x30 /* 0 */;
            state.Advance();
            return true;
        }
        state.LastIntValue = 0;
        return false;
    }

    private static bool IsOctalDigit(int ch)
    {
        return ch >= 0x30 /* 0 */ && ch <= 0x37 /* 7 */;
    }

    // https://www.ecma-international.org/ecma-262/8.0/#prod-Hex4Digits
    // https://www.ecma-international.org/ecma-262/8.0/#prod-HexDigit
    // And HexDigit HexDigit in https://www.ecma-international.org/ecma-262/8.0/#prod-HexEscapeSequence
    public bool RegexpEatFixedHexDigits(RegExpValidationState state, int length)
    {
        int start = state.Pos;
        state.LastIntValue = 0;
        for (int i = 0; i < length; ++i)
        {
            int ch = state.Current();
            if (!IsHexDigit(ch))
            {
                state.Pos = start;
                return false;
            }
            state.LastIntValue = 16 * state.LastIntValue + HexToInt(ch);
            state.Advance();
        }
        return true;
    }
}

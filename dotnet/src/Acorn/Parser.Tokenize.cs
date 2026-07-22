using System.Globalization;
using System.Numerics;
using System.Text;
using tt = Acorn.TokenTypes;

namespace Acorn;

public partial class Parser
{
    /// <summary>charCodeAt equivalent; returns -1 when out of range (mimics NaN comparisons).</summary>
    private int CharCodeAt(int p) => p >= 0 && p < Input.Length ? Input[p] : -1;

    // Move to the next token.
    public virtual void Next(bool ignoreEscapeSequenceInKeyword = false)
    {
        if (!ignoreEscapeSequenceInKeyword && Type.Keyword != null && ContainsEsc)
            RaiseRecoverable(Start, "Escape sequence in keyword " + Type.Keyword);
        if (Options.OnToken != null || Options.OnTokenList != null)
        {
            var token = new Token(this);
            Options.OnToken?.Invoke(token);
            Options.OnTokenList?.Add(token);
        }

        LastTokEnd = End;
        LastTokStart = Start;
        LastTokEndLoc = EndLoc;
        LastTokStartLoc = StartLoc;
        NextToken();
    }

    public Token GetToken()
    {
        Next();
        return new Token(this);
    }

    // Read a single token, updating the parser object's token-related properties.
    public void NextToken()
    {
        TokContext curContext = CurContext();
        if (curContext == null || !curContext.PreserveSpace) SkipSpace();

        Start = Pos;
        if (Options.Locations) StartLoc = CurPosition();
        if (Pos >= Input.Length) { FinishToken(tt.Eof); return; }

        if (curContext!.Override != null) curContext.Override(this);
        else ReadToken(FullCharCodeAtPos());
    }

    public void ReadToken(int code)
    {
        // Identifier or keyword. '\uXXXX' sequences are allowed in identifiers.
        if (Identifier.IsIdentifierStart(code, Options.EcmaVersion >= 6) || code == 92 /* '\' */)
        { ReadWord(); return; }

        GetTokenFromCode(code);
    }

    public int FullCharCodeAt(int pos)
    {
        int code = CharCodeAt(pos);
        if (code <= 0xd7ff || code >= 0xdc00) return code;
        int next = CharCodeAt(pos + 1);
        return next <= 0xdbff || next >= 0xe000 ? code : (code << 10) + next - 0x35fdc00;
    }

    public int FullCharCodeAtPos() => FullCharCodeAt(Pos);

    public void SkipBlockComment()
    {
        Position? startLoc = Options.OnComment != null || Options.OnCommentList != null ? CurPosition() : null;
        int start = Pos;
        Pos += 2;
        int end = Input.IndexOf("*/", Pos, StringComparison.Ordinal);
        if (end == -1) Raise(Pos - 2, "Unterminated comment");
        Pos = end + 2;
        if (Options.Locations)
        {
            int pos = start;
            int nextBreak;
            while ((nextBreak = Whitespace.NextLineBreak(Input, pos, Pos)) > -1)
            {
                ++CurLine;
                pos = LineStart = nextBreak;
            }
        }
        EmitComment(true, Input.Substring(start + 2, end - (start + 2)), start, Pos, startLoc, CurPosition());
    }

    public void SkipLineComment(int startSkip)
    {
        int start = Pos;
        Position? startLoc = Options.OnComment != null || Options.OnCommentList != null ? CurPosition() : null;
        Pos += startSkip;
        int ch = CharCodeAt(Pos);
        while (Pos < Input.Length && !Whitespace.IsNewLine(ch))
        {
            ch = CharCodeAt(++Pos);
        }
        EmitComment(false, Input.Substring(start + startSkip, Pos - (start + startSkip)), start, Pos, startLoc, CurPosition());
    }

    private void EmitComment(bool block, string text, int start, int end, Position? startLoc, Position? endLoc)
    {
        Options.OnComment?.Invoke(block, text, start, end, startLoc, endLoc);
        if (Options.OnCommentList != null)
        {
            var comment = new Comment { Type = block ? "Block" : "Line", Value = text, Start = start, End = end };
            if (Options.Locations) comment.Loc = new SourceLocation(this, startLoc, endLoc);
            if (Options.Ranges) comment.Range = new[] { start, end };
            Options.OnCommentList.Add(comment);
        }
    }

    // Called at the start of the parse and after every token. Skips whitespace and comments.
    public void SkipSpace()
    {
        while (Pos < Input.Length)
        {
            int ch = CharCodeAt(Pos);
            switch (ch)
            {
                case 32:
                case 160: // ' '
                    ++Pos;
                    break;
                case 13:
                    if (CharCodeAt(Pos + 1) == 10) ++Pos;
                    goto case 10;
                case 10:
                case 8232:
                case 8233:
                    ++Pos;
                    if (Options.Locations) { ++CurLine; LineStart = Pos; }
                    break;
                case 47: // '/'
                    switch (CharCodeAt(Pos + 1))
                    {
                        case 42: // '*'
                            SkipBlockComment();
                            break;
                        case 47:
                            SkipLineComment(2);
                            break;
                        default:
                            return;
                    }
                    break;
                default:
                    if (ch > 8 && ch < 14 || ch >= 5760 && Whitespace.NonASCIIwhitespace.IsMatch(((char)ch).ToString()))
                    {
                        ++Pos;
                    }
                    else
                    {
                        return;
                    }
                    break;
            }
        }
    }

    // Called at the end of every token.
    public void FinishToken(TokenType type, object? val = null)
    {
        End = Pos;
        if (Options.Locations) EndLoc = CurPosition();
        TokenType prevType = Type;
        Type = type;
        Value = val;

        UpdateContext(prevType);
    }

    // ### Token reading

    public void ReadToken_dot()
    {
        int next = CharCodeAt(Pos + 1);
        if (next >= 48 && next <= 57) { ReadNumber(true); return; }
        int next2 = CharCodeAt(Pos + 2);
        if (Options.EcmaVersion >= 6 && next == 46 && next2 == 46) // 46 = dot '.'
        {
            Pos += 3;
            FinishToken(tt.Ellipsis);
        }
        else
        {
            ++Pos;
            FinishToken(tt.Dot);
        }
    }

    public void ReadToken_slash() // '/'
    {
        int next = CharCodeAt(Pos + 1);
        if (ExprAllowed) { ++Pos; ReadRegexp(); return; }
        if (next == 61) { FinishOp(tt.Assign, 2); return; }
        FinishOp(tt.Slash, 1);
    }

    public void ReadToken_mult_modulo_exp(int code) // '%*'
    {
        int next = CharCodeAt(Pos + 1);
        int size = 1;
        TokenType tokentype = code == 42 ? tt.Star : tt.Modulo;

        // exponentiation operator ** and **=
        if (Options.EcmaVersion >= 7 && code == 42 && next == 42)
        {
            ++size;
            tokentype = tt.StarStar;
            next = CharCodeAt(Pos + 2);
        }

        if (next == 61) { FinishOp(tt.Assign, size + 1); return; }
        FinishOp(tokentype, size);
    }

    public void ReadToken_pipe_amp(int code) // '|&'
    {
        int next = CharCodeAt(Pos + 1);
        if (next == code)
        {
            if (Options.EcmaVersion >= 12)
            {
                int next2 = CharCodeAt(Pos + 2);
                if (next2 == 61) { FinishOp(tt.Assign, 3); return; }
            }
            FinishOp(code == 124 ? tt.LogicalOR : tt.LogicalAND, 2);
            return;
        }
        if (next == 61) { FinishOp(tt.Assign, 2); return; }
        FinishOp(code == 124 ? tt.BitwiseOR : tt.BitwiseAND, 1);
    }

    public void ReadToken_caret() // '^'
    {
        int next = CharCodeAt(Pos + 1);
        if (next == 61) { FinishOp(tt.Assign, 2); return; }
        FinishOp(tt.BitwiseXOR, 1);
    }

    public void ReadToken_plus_min(int code) // '+-'
    {
        int next = CharCodeAt(Pos + 1);
        if (next == code)
        {
            if (next == 45 && !InModule && CharCodeAt(Pos + 2) == 62 &&
                (LastTokEnd == 0 || Whitespace.LineBreak.IsMatch(Input.Substring(LastTokEnd, Pos - LastTokEnd))))
            {
                // A `-->` line comment
                SkipLineComment(3);
                SkipSpace();
                NextToken();
                return;
            }
            FinishOp(tt.IncDec, 2);
            return;
        }
        if (next == 61) { FinishOp(tt.Assign, 2); return; }
        FinishOp(tt.PlusMin, 1);
    }

    public void ReadToken_lt_gt(int code) // '<>'
    {
        int next = CharCodeAt(Pos + 1);
        int size = 1;
        if (next == code)
        {
            size = code == 62 && CharCodeAt(Pos + 2) == 62 ? 3 : 2;
            if (CharCodeAt(Pos + size) == 61) { FinishOp(tt.Assign, size + 1); return; }
            FinishOp(tt.BitShift, size);
            return;
        }
        if (next == 33 && code == 60 && !InModule && CharCodeAt(Pos + 2) == 45 &&
            CharCodeAt(Pos + 3) == 45)
        {
            // `<!--`, an XML-style comment treated as a line comment
            SkipLineComment(4);
            SkipSpace();
            NextToken();
            return;
        }
        if (next == 61) size = 2;
        FinishOp(tt.Relational, size);
    }

    public void ReadToken_eq_excl(int code) // '=!'
    {
        int next = CharCodeAt(Pos + 1);
        if (next == 61) { FinishOp(tt.Equality, CharCodeAt(Pos + 2) == 61 ? 3 : 2); return; }
        if (code == 61 && next == 62 && Options.EcmaVersion >= 6) // '=>'
        {
            Pos += 2;
            FinishToken(tt.Arrow);
            return;
        }
        FinishOp(code == 61 ? tt.Eq : tt.Prefix, 1);
    }

    public void ReadToken_question() // '?'
    {
        int ecmaVersion = Options.EcmaVersion;
        if (ecmaVersion >= 11)
        {
            int next = CharCodeAt(Pos + 1);
            if (next == 46)
            {
                int next2 = CharCodeAt(Pos + 2);
                if (next2 < 48 || next2 > 57) { FinishOp(tt.QuestionDot, 2); return; }
            }
            if (next == 63)
            {
                if (ecmaVersion >= 12)
                {
                    int next2 = CharCodeAt(Pos + 2);
                    if (next2 == 61) { FinishOp(tt.Assign, 3); return; }
                }
                FinishOp(tt.Coalesce, 2);
                return;
            }
        }
        FinishOp(tt.Question, 1);
    }

    public void ReadToken_numberSign() // '#'
    {
        int ecmaVersion = Options.EcmaVersion;
        int code = 35; // '#'
        if (ecmaVersion >= 13)
        {
            ++Pos;
            code = FullCharCodeAtPos();
            if (Identifier.IsIdentifierStart(code, true) || code == 92 /* '\' */)
            {
                FinishToken(tt.PrivateId, ReadWord1());
                return;
            }
        }

        Raise(Pos, "Unexpected character '" + Util.CodePointToString(code) + "'");
    }

    public void GetTokenFromCode(int code)
    {
        switch (code)
        {
            case 46: // '.'
                ReadToken_dot();
                return;

            // Punctuation tokens.
            case 40: ++Pos; FinishToken(tt.ParenL); return;
            case 41: ++Pos; FinishToken(tt.ParenR); return;
            case 59: ++Pos; FinishToken(tt.Semi); return;
            case 44: ++Pos; FinishToken(tt.Comma); return;
            case 91: ++Pos; FinishToken(tt.BracketL); return;
            case 93: ++Pos; FinishToken(tt.BracketR); return;
            case 123: ++Pos; FinishToken(tt.BraceL); return;
            case 125: ++Pos; FinishToken(tt.BraceR); return;
            case 58: ++Pos; FinishToken(tt.Colon); return;

            case 96: // '`'
                if (Options.EcmaVersion < 6) break;
                ++Pos;
                FinishToken(tt.BackQuote);
                return;

            case 48: // '0'
            {
                int next = CharCodeAt(Pos + 1);
                if (next == 120 || next == 88) { ReadRadixNumber(16); return; } // '0x', '0X'
                if (Options.EcmaVersion >= 6)
                {
                    if (next == 111 || next == 79) { ReadRadixNumber(8); return; } // '0o', '0O'
                    if (next == 98 || next == 66) { ReadRadixNumber(2); return; } // '0b', '0B'
                }
                goto case 49;
            }

            case 49:
            case 50:
            case 51:
            case 52:
            case 53:
            case 54:
            case 55:
            case 56:
            case 57: // 1-9
                ReadNumber(false);
                return;

            case 34:
            case 39: // '"', "'"
                ReadString(code);
                return;

            case 47: // '/'
                ReadToken_slash();
                return;

            case 37:
            case 42: // '%*'
                ReadToken_mult_modulo_exp(code);
                return;

            case 124:
            case 38: // '|&'
                ReadToken_pipe_amp(code);
                return;

            case 94: // '^'
                ReadToken_caret();
                return;

            case 43:
            case 45: // '+-'
                ReadToken_plus_min(code);
                return;

            case 60:
            case 62: // '<>'
                ReadToken_lt_gt(code);
                return;

            case 61:
            case 33: // '=!'
                ReadToken_eq_excl(code);
                return;

            case 63: // '?'
                ReadToken_question();
                return;

            case 126: // '~'
                FinishOp(tt.Prefix, 1);
                return;

            case 35: // '#'
                ReadToken_numberSign();
                return;
        }

        Raise(Pos, "Unexpected character '" + Util.CodePointToString(code) + "'");
    }

    public void FinishOp(TokenType type, int size)
    {
        string str = Input.Substring(Pos, size);
        Pos += size;
        FinishToken(type, str);
    }

    public void ReadRegexp()
    {
        bool escaped = false, inClass = false;
        int start = Pos;
        for (; ; )
        {
            if (Pos >= Input.Length) Raise(start, "Unterminated regular expression");
            char ch = Input[Pos];
            if (Whitespace.LineBreak.IsMatch(ch.ToString())) Raise(start, "Unterminated regular expression");
            if (!escaped)
            {
                if (ch == '[') inClass = true;
                else if (ch == ']' && inClass) inClass = false;
                else if (ch == '/' && !inClass) break;
                escaped = ch == '\\';
            }
            else escaped = false;
            ++Pos;
        }
        string pattern = Input.Substring(start, Pos - start);
        ++Pos;
        int flagsStart = Pos;
        string flags = ReadWord1();
        if (ContainsEsc) Unexpected(flagsStart);

        // Validate pattern
        RegExpValidationState state = RegexpState ??= new RegExpValidationState(this);
        state.Reset(start, pattern, flags);
        ValidateRegExpFlags(state);
        ValidateRegExpPattern(state);

        // Create Literal#value property value.
        object? value = null;
        try
        {
            value = new RegExpLiteralValue(pattern, flags);
        }
        catch
        {
            // ESTree requires null if it failed to instantiate a RegExp object.
        }

        FinishToken(tt.Regexp, new RegexpTokenValue(pattern, flags, value));
    }

    // Read an integer in the given radix. Return null if zero digits were read.
    public double? ReadInt(int radix, int? len = null, bool maybeLegacyOctalNumericLiteral = false)
    {
        bool allowSeparators = Options.EcmaVersion >= 12 && len == null;

        bool isLegacyOctalNumericLiteral = maybeLegacyOctalNumericLiteral && CharCodeAt(Pos) == 48;

        int start = Pos;
        double total = 0;
        int lastCode = 0;
        for (int i = 0, e = len == null ? int.MaxValue : len.Value; i < e; ++i, ++Pos)
        {
            int code = CharCodeAt(Pos);
            int val;

            if (allowSeparators && code == 95)
            {
                if (isLegacyOctalNumericLiteral) RaiseRecoverable(Pos, "Numeric separator is not allowed in legacy octal numeric literals");
                if (lastCode == 95) RaiseRecoverable(Pos, "Numeric separator must be exactly one underscore");
                if (i == 0) RaiseRecoverable(Pos, "Numeric separator is not allowed at the first of digits");
                lastCode = code;
                continue;
            }

            if (code >= 97) val = code - 97 + 10; // a
            else if (code >= 65) val = code - 65 + 10; // A
            else if (code >= 48 && code <= 57) val = code - 48; // 0-9
            else val = int.MaxValue;
            if (val >= radix) break;
            lastCode = code;
            total = total * radix + val;
        }

        if (allowSeparators && lastCode == 95) RaiseRecoverable(Pos - 1, "Numeric separator is not allowed at the last of digits");
        if (Pos == start || (len != null && Pos - start != len)) return null;

        return total;
    }

    private static double StringToNumber(string str, bool isLegacyOctalNumericLiteral)
    {
        if (isLegacyOctalNumericLiteral)
        {
            return ParseIntRadix(str, 8);
        }
        return double.Parse(str.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static double ParseIntRadix(string str, int radix)
    {
        double total = 0;
        foreach (char c in str)
        {
            int val;
            if (c >= 'a') val = c - 'a' + 10;
            else if (c >= 'A') val = c - 'A' + 10;
            else if (c >= '0' && c <= '9') val = c - '0';
            else break;
            if (val >= radix) break;
            total = total * radix + val;
        }
        return total;
    }

    private static BigInteger StringToBigInt(string str)
    {
        return BigInteger.Parse(str.Replace("_", ""), CultureInfo.InvariantCulture);
    }

    public void ReadRadixNumber(int radix)
    {
        int start = Pos;
        Pos += 2; // 0x
        double? val = ReadInt(radix);
        if (val == null) Raise(Start + 2, "Expected number in radix " + radix);
        object? value = val;
        if (Options.EcmaVersion >= 11 && CharCodeAt(Pos) == 110)
        {
            value = ParseBigIntRadix(Input.Substring(start, Pos - start), radix);
            ++Pos;
        }
        else if (Identifier.IsIdentifierStart(FullCharCodeAtPos())) Raise(Pos, "Identifier directly after number");
        FinishToken(tt.Num, value);
    }

    private static BigInteger ParseBigIntRadix(string literal, int radix)
    {
        // literal includes the 0x/0o/0b prefix; strip it and parse in the radix.
        string digits = literal.Length >= 2 ? literal.Substring(2) : literal;
        digits = digits.Replace("_", "");
        BigInteger total = 0;
        foreach (char c in digits)
        {
            int v;
            if (c >= 'a') v = c - 'a' + 10;
            else if (c >= 'A') v = c - 'A' + 10;
            else v = c - '0';
            total = total * radix + v;
        }
        return total;
    }

    // Read an integer, octal integer, or floating-point number.
    public void ReadNumber(bool startsWithDot)
    {
        int start = Pos;
        if (!startsWithDot && ReadInt(10, null, true) == null) Raise(start, "Invalid number");
        bool octal = Pos - start >= 2 && CharCodeAt(start) == 48;
        if (octal && Strict) Raise(start, "Invalid number");
        int next = CharCodeAt(Pos);
        if (!octal && !startsWithDot && Options.EcmaVersion >= 11 && next == 110)
        {
            BigInteger bigVal = StringToBigInt(Input.Substring(start, Pos - start));
            ++Pos;
            if (Identifier.IsIdentifierStart(FullCharCodeAtPos())) Raise(Pos, "Identifier directly after number");
            FinishToken(tt.Num, bigVal);
            return;
        }
        if (octal && System.Text.RegularExpressions.Regex.IsMatch(Input.Substring(start, Pos - start), "[89]")) octal = false;
        if (next == 46 && !octal) // '.'
        {
            ++Pos;
            ReadInt(10);
            next = CharCodeAt(Pos);
        }
        if ((next == 69 || next == 101) && !octal) // 'eE'
        {
            next = CharCodeAt(++Pos);
            if (next == 43 || next == 45) ++Pos; // '+-'
            if (ReadInt(10) == null) Raise(start, "Invalid number");
        }
        if (Identifier.IsIdentifierStart(FullCharCodeAtPos())) Raise(Pos, "Identifier directly after number");

        double dval = StringToNumber(Input.Substring(start, Pos - start), octal);
        FinishToken(tt.Num, dval);
    }

    // Read a string value, interpreting backslash-escapes.
    public int ReadCodePoint()
    {
        int ch = CharCodeAt(Pos);
        int code;

        if (ch == 123) // '{'
        {
            if (Options.EcmaVersion < 6) Unexpected();
            int codePos = ++Pos;
            code = ReadHexChar(Input.IndexOf("}", Pos, StringComparison.Ordinal) - Pos);
            ++Pos;
            if (code > 0x10FFFF) InvalidStringToken(codePos, "Code point out of bounds");
        }
        else
        {
            code = ReadHexChar(4);
        }
        return code;
    }

    public void ReadString(int quote)
    {
        StringBuilder outSb = new StringBuilder();
        int chunkStart = ++Pos;
        for (; ; )
        {
            if (Pos >= Input.Length) Raise(Start, "Unterminated string constant");
            int ch = CharCodeAt(Pos);
            if (ch == quote) break;
            if (ch == 92) // '\'
            {
                outSb.Append(Input, chunkStart, Pos - chunkStart);
                outSb.Append(ReadEscapedChar(false));
                chunkStart = Pos;
            }
            else if (ch == 0x2028 || ch == 0x2029)
            {
                if (Options.EcmaVersion < 10) Raise(Start, "Unterminated string constant");
                ++Pos;
                if (Options.Locations) { CurLine++; LineStart = Pos; }
            }
            else
            {
                if (Whitespace.IsNewLine(ch)) Raise(Start, "Unterminated string constant");
                ++Pos;
            }
        }
        outSb.Append(Input, chunkStart, Pos - chunkStart);
        ++Pos;
        FinishToken(tt.String, outSb.ToString());
    }

    // Reads template string tokens.
    public void TryReadTemplateToken()
    {
        InTemplateElement = true;
        try
        {
            ReadTmplToken();
        }
        catch (InvalidTemplateEscapeException)
        {
            ReadInvalidTemplateToken();
        }

        InTemplateElement = false;
    }

    public void InvalidStringToken(int position, string message)
    {
        if (InTemplateElement && Options.EcmaVersion >= 9)
        {
            throw new InvalidTemplateEscapeException();
        }
        else
        {
            Raise(position, message);
        }
    }

    public void ReadTmplToken()
    {
        StringBuilder outSb = new StringBuilder();
        int chunkStart = Pos;
        for (; ; )
        {
            if (Pos >= Input.Length) Raise(Start, "Unterminated template");
            int ch = CharCodeAt(Pos);
            if (ch == 96 || ch == 36 && CharCodeAt(Pos + 1) == 123) // '`', '${'
            {
                if (Pos == Start && (Type == tt.Template || Type == tt.InvalidTemplate))
                {
                    if (ch == 36)
                    {
                        Pos += 2;
                        FinishToken(tt.DollarBraceL);
                        return;
                    }
                    else
                    {
                        ++Pos;
                        FinishToken(tt.BackQuote);
                        return;
                    }
                }
                outSb.Append(Input, chunkStart, Pos - chunkStart);
                FinishToken(tt.Template, outSb.ToString());
                return;
            }
            if (ch == 92) // '\'
            {
                outSb.Append(Input, chunkStart, Pos - chunkStart);
                outSb.Append(ReadEscapedChar(true));
                chunkStart = Pos;
            }
            else if (Whitespace.IsNewLine(ch))
            {
                outSb.Append(Input, chunkStart, Pos - chunkStart);
                ++Pos;
                switch (ch)
                {
                    case 13:
                        if (CharCodeAt(Pos) == 10) ++Pos;
                        goto case 10;
                    case 10:
                        outSb.Append('\n');
                        break;
                    default:
                        outSb.Append((char)ch);
                        break;
                }
                if (Options.Locations) { ++CurLine; LineStart = Pos; }
                chunkStart = Pos;
            }
            else
            {
                ++Pos;
            }
        }
    }

    // Reads a template token to search for the end, without validating escapes.
    public void ReadInvalidTemplateToken()
    {
        for (; Pos < Input.Length; Pos++)
        {
            switch (Input[Pos])
            {
                case '\\':
                    ++Pos;
                    break;

                case '$':
                    if (Pos + 1 >= Input.Length || Input[Pos + 1] != '{') break;
                    goto case '`';
                case '`':
                    FinishToken(tt.InvalidTemplate, Input.Substring(Start, Pos - Start));
                    return;

                case '\r':
                    if (Pos + 1 < Input.Length && Input[Pos + 1] == '\n') ++Pos;
                    goto case '\n';
                case '\n':
                case '\u2028':
                case '\u2029':
                    ++CurLine;
                    LineStart = Pos + 1;
                    break;
            }
        }
        Raise(Start, "Unterminated template");
    }

    // Used to read escaped characters
    public string ReadEscapedChar(bool inTemplate)
    {
        int ch = CharCodeAt(++Pos);
        ++Pos;
        switch (ch)
        {
            case 110: return "\n"; // 'n' -> '\n'
            case 114: return "\r"; // 'r' -> '\r'
            case 120: return ((char)ReadHexChar(2)).ToString(); // 'x'
            case 117: return Util.CodePointToString(ReadCodePoint()); // 'u'
            case 116: return "\t"; // 't' -> '\t'
            case 98: return "\b"; // 'b' -> '\b'
            case 118: return "\u000b"; // 'v' -> vertical tab
            case 102: return "\f"; // 'f' -> '\f'
            case 13:
                if (CharCodeAt(Pos) == 10) ++Pos; // '\r\n'
                goto case 10;
            case 10: // ' \n'
                if (Options.Locations) { LineStart = Pos; ++CurLine; }
                return "";
            case 56:
            case 57:
                if (Strict)
                {
                    InvalidStringToken(Pos - 1, "Invalid escape sequence");
                }
                if (inTemplate)
                {
                    int codePos = Pos - 1;
                    InvalidStringToken(codePos, "Invalid escape sequence in template string");
                }
                goto default;
            default:
                if (ch >= 48 && ch <= 55)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(SubstrSafe(Pos - 1, 3), "^[0-7]+");
                    string octalStr = m.Value;
                    int octal = (int)ParseIntRadix(octalStr, 8);
                    if (octal > 255)
                    {
                        octalStr = octalStr.Substring(0, octalStr.Length - 1);
                        octal = (int)ParseIntRadix(octalStr, 8);
                    }
                    Pos += octalStr.Length - 1;
                    ch = CharCodeAt(Pos);
                    if ((octalStr != "0" || ch == 56 || ch == 57) && (Strict || inTemplate))
                    {
                        InvalidStringToken(
                            Pos - 1 - octalStr.Length,
                            inTemplate
                                ? "Octal literal in template string"
                                : "Octal literal in strict mode");
                    }
                    return ((char)octal).ToString();
                }
                if (Whitespace.IsNewLine(ch))
                {
                    if (Options.Locations) { LineStart = Pos; ++CurLine; }
                    return "";
                }
                return ((char)ch).ToString();
        }
    }

    private string SubstrSafe(int start, int len)
    {
        if (start < 0) start = 0;
        if (start >= Input.Length) return "";
        int avail = Math.Min(len, Input.Length - start);
        return Input.Substring(start, avail);
    }

    // Used to read character escape sequences ('\x', '\u', '\U').
    public int ReadHexChar(int len)
    {
        int codePos = Pos;
        double? n = ReadInt(16, len);
        if (n == null) InvalidStringToken(codePos, "Bad character escape sequence");
        return (int)(n ?? 0);
    }

    // Read an identifier, and return it as a string. Sets containsEsc.
    public string ReadWord1()
    {
        ContainsEsc = false;
        StringBuilder word = new StringBuilder();
        bool first = true;
        int chunkStart = Pos;
        bool astral = Options.EcmaVersion >= 6;
        while (Pos < Input.Length)
        {
            int ch = FullCharCodeAtPos();
            if (Identifier.IsIdentifierChar(ch, astral))
            {
                Pos += ch <= 0xffff ? 1 : 2;
            }
            else if (ch == 92) // "\"
            {
                ContainsEsc = true;
                word.Append(Input, chunkStart, Pos - chunkStart);
                int escStart = Pos;
                if (CharCodeAt(++Pos) != 117) // "u"
                    InvalidStringToken(Pos, "Expecting Unicode escape sequence \\uXXXX");
                ++Pos;
                int esc = ReadCodePoint();
                if (!(first ? Identifier.IsIdentifierStart(esc, astral) : Identifier.IsIdentifierChar(esc, astral)))
                    InvalidStringToken(escStart, "Invalid Unicode escape");
                word.Append(Util.CodePointToString(esc));
                chunkStart = Pos;
            }
            else
            {
                break;
            }
            first = false;
        }
        word.Append(Input, chunkStart, Pos - chunkStart);
        return word.ToString();
    }

    // Read an identifier or keyword token.
    public void ReadWord()
    {
        string word = ReadWord1();
        TokenType type = tt.Name;
        if (Keywords.IsMatch(word))
        {
            type = TokenTypes.Keywords[word];
        }
        FinishToken(type, word);
    }
}

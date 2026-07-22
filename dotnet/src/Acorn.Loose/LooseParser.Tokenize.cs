using System.Text.RegularExpressions;
using Acorn;
using tt = Acorn.TokenTypes;

namespace Acorn.Loose;

// Ported from acorn-loose/src/tokenize.js
public partial class LooseParser
{
    private static bool IsSpace(int ch) =>
        (ch < 14 && ch > 8) || ch == 32 || ch == 160 || Whitespace.IsNewLine(ch);

    // Error-message classification regexes (from tokenize.js).
    private static readonly Regex ReUnterminated = new("unterminated", RegexOptions.IgnoreCase);
    private static readonly Regex ReString = new("string");
    private static readonly Regex ReRegularExpr = new("regular expr", RegexOptions.IgnoreCase);
    private static readonly Regex ReTemplate = new("template");
    private static readonly Regex ReInvalid = new("invalid (unicode|regexp|number)|expecting unicode|octal literal|is reserved|directly after number|expected number in radix|numeric separator", RegexOptions.IgnoreCase);
    private static readonly Regex ReCharEscape = new("character escape|expected hexadecimal", RegexOptions.IgnoreCase);
    private static readonly Regex ReUnexpectedChar = new("unexpected character", RegexOptions.IgnoreCase);
    private static readonly Regex ReRegularExpression = new("regular expression", RegexOptions.IgnoreCase);

    // resetTo exprAllowed heuristics.
    private static readonly Regex ReExprAllowedChar = new("[\\[{(,;:?/*=+\\-~!|&%^<>]");
    private static readonly Regex ReExprAllowedWordChar = new("[enwfd]");
    private static readonly Regex ReExprAllowedKeyword = new("\\b(case|else|return|throw|new|in|(instance|type)?of|delete|void)$");

    public void Next()
    {
        this.last = this.tok;
        if (this.ahead.Count > 0)
        {
            this.tok = this.ahead[0];
            this.ahead.RemoveAt(0);
        }
        else
        {
            this.tok = this.ReadToken();
        }

        if (this.tok.Start >= this.nextLineStart)
        {
            while (this.tok.Start >= this.nextLineStart)
            {
                this.curLineStart = this.nextLineStart;
                this.nextLineStart = this.LineEnd(this.curLineStart) + 1;
            }
            this.curIndent = this.IndentationAfter(this.curLineStart);
        }
    }

    private LooseToken FromToks()
    {
        var t = new LooseToken
        {
            Type = this.toks.Type,
            Value = this.toks.Value,
            Start = this.toks.Start,
            End = this.toks.End
        };
        if (this.options.Locations)
            t.Loc = new SourceLocation(this.toks, this.toks.StartLoc, this.toks.EndLoc);
        return t;
    }

    public LooseToken ReadToken()
    {
        for (; ; )
        {
            try
            {
                this.toks.Next();
                if (this.toks.Type == tt.Dot &&
                    this.toks.End < this.input.Length && this.input[this.toks.End] == '.' &&
                    this.options.EcmaVersion >= 6)
                {
                    this.toks.End++;
                    this.toks.Type = tt.Ellipsis;
                }
                return this.FromToks();
            }
            catch (AcornSyntaxError e)
            {
                // Try to skip some text, based on the error message, and then continue.
                string msg = e.Message;
                int pos = e.RaisedAt;
                bool replaceTrue = true;         // JS `replace = true`
                LooseToken? replaceObj = null;   // JS `replace` set to an object

                if (ReUnterminated.IsMatch(msg))
                {
                    pos = this.LineEnd(e.Pos + 1);
                    if (ReString.IsMatch(msg))
                    {
                        replaceTrue = false;
                        replaceObj = new LooseToken
                        {
                            Start = e.Pos,
                            End = pos,
                            Type = tt.String,
                            Value = Slice(this.input, e.Pos + 1, pos)
                        };
                    }
                    else if (ReRegularExpr.IsMatch(msg))
                    {
                        string reSrc = Slice(this.input, e.Pos, pos);
                        object? reVal = TryMakeRegExp(reSrc);
                        replaceTrue = false;
                        replaceObj = new LooseToken
                        {
                            Start = e.Pos,
                            End = pos,
                            Type = tt.Regexp,
                            Value = reVal
                        };
                    }
                    else if (ReTemplate.IsMatch(msg))
                    {
                        replaceTrue = false;
                        replaceObj = new LooseToken
                        {
                            Start = e.Pos,
                            End = pos,
                            Type = tt.Template,
                            Value = Slice(this.input, e.Pos, pos)
                        };
                    }
                    else
                    {
                        replaceTrue = false;
                        replaceObj = null; // JS `replace = false`
                    }
                }
                else if (ReInvalid.IsMatch(msg))
                {
                    while (pos < this.input.Length && !IsSpace(this.input[pos])) ++pos;
                }
                else if (ReCharEscape.IsMatch(msg))
                {
                    while (pos < this.input.Length)
                    {
                        int ch = this.input[pos++];
                        if (ch == 34 || ch == 39 || Whitespace.IsNewLine(ch)) break;
                    }
                }
                else if (ReUnexpectedChar.IsMatch(msg))
                {
                    pos++;
                    replaceTrue = false;
                    replaceObj = null; // JS `replace = false`
                }
                else if (ReRegularExpression.IsMatch(msg))
                {
                    replaceTrue = true;
                    replaceObj = null;
                }
                else
                {
                    throw;
                }

                this.ResetTo(pos);
                if (replaceTrue)
                    replaceObj = new LooseToken { Start = pos, End = pos, Type = tt.Name, Value = ParseUtil.DummyValue };
                if (replaceObj != null)
                {
                    if (this.options.Locations)
                        replaceObj.Loc = new SourceLocation(
                            this.toks,
                            LocUtil.GetLineInfo(this.input, replaceObj.Start),
                            LocUtil.GetLineInfo(this.input, replaceObj.End));
                    return replaceObj;
                }
            }
        }
    }

    // JS `try { re = new RegExp(re) } catch (e) {}` returns a value carrying
    // pattern/flags/value read later by parseExprAtom. The recovered pattern has
    // no `.pattern`/`.value` (it is a RegExp instance or a raw string), so those
    // read back as undefined; only `.flags` ("") survives when the body compiles.
    private static object TryMakeRegExp(string body)
    {
        try
        {
            _ = new Regex(body);
            return new RegexpTokenValue(null!, "", null); // compiled: flags "", pattern/value undefined
        }
        catch
        {
            return new RegexpTokenValue(null!, null!, null); // did not compile: all undefined
        }
    }

    public void ResetTo(int pos)
    {
        if (this.options.Locations)
        {
            if (pos >= this.toks.Pos)
            {
                string skipped = Slice(this.input, this.toks.Pos, pos);
                foreach (Match match in Whitespace.LineBreakG.Matches(skipped))
                {
                    ++this.toks.CurLine;
                    this.toks.LineStart = pos + match.Index + match.Length;
                }
            }
            else
            {
                this.toks.CurLine = 1;
                this.toks.LineStart = 0;
                foreach (Match match in Whitespace.LineBreakG.Matches(this.input))
                {
                    if (match.Index >= pos) break;
                    ++this.toks.CurLine;
                    this.toks.LineStart = match.Index + match.Length;
                }
            }
        }

        this.toks.Pos = pos;
        this.toks.ContainsEsc = false;
        string ch = (pos - 1 >= 0 && pos - 1 < this.input.Length) ? this.input[pos - 1].ToString() : "";
        this.toks.ExprAllowed = ch.Length == 0 || ReExprAllowedChar.IsMatch(ch) ||
            (ReExprAllowedWordChar.IsMatch(ch) &&
             ReExprAllowedKeyword.IsMatch(Slice(this.input, pos - 10, pos)));
    }

    public LooseToken LookAhead(int n)
    {
        while (n > this.ahead.Count)
            this.ahead.Add(this.ReadToken());
        return this.ahead[n - 1];
    }

    private static string Slice(string s, int from, int to)
    {
        if (from < 0) from = 0;
        if (to > s.Length) to = s.Length;
        if (from >= to) return "";
        return s.Substring(from, to - from);
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenScct.Editor.Core.Interop.T3d;

/// <summary>
/// Parses UE2 property text: <c>Name=Value</c>, <c>Name(i)=Value</c>, structs, arrays, quoted strings and
/// <c>Class'Path'</c> references. Lenient: what it cannot read becomes a bare token, so nothing is lost.
/// </summary>
public static partial class T3dValueParser
{
    /// <summary>
    /// Removes the name-index notes the SCCT editor appends to every name it exports (<c>§(1155)</c>, the
    /// character 0xA7 then the global FName index), which no importer reads. Quoted strings are left as they are:
    /// a note there is the string's own text.
    /// </summary>
    public static string StripNameNotes(string line)
    {
        if (!line.Contains('§'))
        {
            return line;
        }

        var sb = new StringBuilder(line.Length);
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted && c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
            {
                sb.Append(c).Append(line[++i]);
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '§' && NameNote().Match(line, i) is { Success: true } note)
            {
                i += note.Length - 1;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Parses one property line (<c>Name=Value</c> or <c>Name(i)=Value</c>), or null when it is not one.</summary>
    public static T3dField? ParseField(string line)
    {
        var r = new Reader(line.Trim());
        return r.Field(topLevel: true);
    }

    /// <summary>Parses a value on its own.</summary>
    public static T3dValue ParseValue(string text) => new Reader(text.Trim()).Value(topLevel: true);

    [GeneratedRegex(@"\G§\(\d+\)")]
    private static partial Regex NameNote();

    private ref struct Reader
    {
        private readonly ReadOnlySpan<char> _s;
        private int _i;

        public Reader(string s)
        {
            _s = s;
            _i = 0;
        }

        public readonly bool AtEnd => _i >= _s.Length;

        private readonly char Peek => _i < _s.Length ? _s[_i] : '\0';

        public T3dField? Field(bool topLevel = false)
        {
            int start = _i;
            string name = Identifier();
            if (name.Length == 0)
            {
                _i = start;
                return null;
            }

            int? index = null;
            if (Peek is '(' or '[')
            {
                char close = Peek == '(' ? ')' : ']';
                int save = _i;
                _i++;
                int digits = _i;
                while (char.IsAsciiDigit(Peek))
                {
                    _i++;
                }

                if (_i > digits && Peek == close)
                {
                    index = int.Parse(_s[digits.._i], NumberStyles.None, CultureInfo.InvariantCulture);
                    _i++;
                }
                else
                {
                    _i = save;
                }
            }

            if (Peek != '=')
            {
                _i = start;
                return null;
            }

            _i++;
            return new T3dField(name, index, Value(topLevel));
        }

        /// <summary>A value. At top level it runs to the end of the line; inside parentheses it stops at , or ).</summary>
        public T3dValue Value(bool topLevel)
        {
            SkipSpaces();
            if (Peek == '(')
            {
                return Group();
            }

            if (Peek == '"')
            {
                return Quoted();
            }

            int start = _i;
            string ident = Identifier();
            if (ident.Length > 0 && Peek == '\'')
            {
                _i++;
                int pathStart = _i;
                while (!AtEnd && Peek != '\'')
                {
                    _i++;
                }

                string path = _s[pathStart.._i].ToString();
                if (!AtEnd)
                {
                    _i++;
                }

                return new T3dRef(ident, path);
            }

            _i = start;
            while (!AtEnd && (topLevel || (Peek != ',' && Peek != ')')))
            {
                _i++;
            }

            return new T3dAtom(_s[start.._i].ToString().Trim());
        }

        private T3dValue Group()
        {
            _i++; // (
            SkipSpaces();
            if (Peek == ')')
            {
                _i++;
                return new T3dList([]);
            }

            if (LooksLikeField())
            {
                var fields = new List<T3dField>();
                while (!AtEnd)
                {
                    SkipSpaces();
                    T3dField? f = Field();
                    if (f is null)
                    {
                        // Unreadable member: keep the rest as one token so the text survives.
                        int start = _i;
                        while (!AtEnd && Peek != ',' && Peek != ')')
                        {
                            _i++;
                        }

                        string junk = _s[start.._i].ToString().Trim();
                        if (junk.Length > 0)
                        {
                            fields.Add(new T3dField(junk, null, new T3dAtom("")));
                        }
                    }
                    else
                    {
                        fields.Add(f);
                    }

                    SkipSpaces();
                    if (Peek == ',')
                    {
                        _i++;
                        continue;
                    }

                    if (Peek == ')')
                    {
                        _i++;
                    }

                    break;
                }

                return new T3dStruct(fields);
            }

            var items = new List<T3dValue>();
            while (!AtEnd)
            {
                items.Add(Value(topLevel: false));
                SkipSpaces();
                if (Peek == ',')
                {
                    _i++;
                    continue;
                }

                if (Peek == ')')
                {
                    _i++;
                }

                break;
            }

            return new T3dList(items);
        }

        /// <summary>True when the text at the cursor is <c>Ident=</c>, <c>Ident(n)=</c> or <c>Ident[n]=</c>.</summary>
        private readonly bool LooksLikeField()
        {
            int i = _i;
            if (i >= _s.Length || !(char.IsAsciiLetter(_s[i]) || _s[i] == '_'))
            {
                return false;
            }

            while (i < _s.Length && (char.IsAsciiLetterOrDigit(_s[i]) || _s[i] == '_'))
            {
                i++;
            }

            if (i < _s.Length && _s[i] is '(' or '[')
            {
                i++;
                while (i < _s.Length && char.IsAsciiDigit(_s[i]))
                {
                    i++;
                }

                if (i < _s.Length && _s[i] is ')' or ']')
                {
                    i++;
                }
            }

            return i < _s.Length && _s[i] == '=';
        }

        private T3dText Quoted()
        {
            _i++; // "
            var sb = new StringBuilder();
            while (!AtEnd && Peek != '"')
            {
                if (Peek == '\\' && _i + 1 < _s.Length && _s[_i + 1] is '"' or '\\')
                {
                    _i++;
                }

                sb.Append(Peek);
                _i++;
            }

            if (!AtEnd)
            {
                _i++;
            }

            return new T3dText(sb.ToString());
        }

        private string Identifier()
        {
            int start = _i;
            if (!(char.IsAsciiLetter(Peek) || Peek == '_'))
            {
                return "";
            }

            while (char.IsAsciiLetterOrDigit(Peek) || Peek == '_')
            {
                _i++;
            }

            return _s[start.._i].ToString();
        }

        private void SkipSpaces()
        {
            while (Peek is ' ' or '\t')
            {
                _i++;
            }
        }
    }
}

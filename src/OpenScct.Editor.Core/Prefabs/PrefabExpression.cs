using System.Globalization;
using OpenScct.Editor.Core.Scripting;

namespace OpenScct.Editor.Core.Prefabs;

/// <summary>
/// The small expression language of prefab templates (<c>${width / 2}</c>). Values are numbers, strings and booleans.
/// Operators: <c>+ - * / %</c> (<c>+</c> joins strings), comparisons <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>&amp;&amp; || !</c>,
/// parentheses, <c>'text'</c> literals, <c>true</c>/<c>false</c>, parameter names, and the functions
/// <c>min max abs floor ceil round sqrt clamp</c>.
/// </summary>
public static class PrefabExpression
{
    public static object Evaluate(string text, IReadOnlyDictionary<string, object> vars, string field)
    {
        var p = new Parser(text, vars, field);
        object value = p.Or();
        p.SkipSpace();
        if (!p.AtEnd)
        {
            throw p.Error($"unexpected '{p.Rest}'");
        }

        return value;
    }

    /// <summary>JavaScript-like truth: false, 0, NaN and "" are false.</summary>
    public static bool Truthy(object v) => v switch
    {
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        _ => false,
    };

    public static string Text(object v) => v switch
    {
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? "",
    };

    private sealed class Parser(string text, IReadOnlyDictionary<string, object> vars, string field)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public string Rest => text[_pos..];

        public ApiException Error(string message) => new($"in ${{{text}}}: {message}", field);

        public object Or()
        {
            object left = And();
            while (Take("||"))
            {
                object right = And();
                left = Truthy(left) || Truthy(right);
            }

            return left;
        }

        private object And()
        {
            object left = Compare();
            while (Take("&&"))
            {
                object right = Compare();
                left = Truthy(left) && Truthy(right);
            }

            return left;
        }

        private object Compare()
        {
            object left = Add();
            foreach (string op in new[] { "==", "!=", "<=", ">=", "<", ">" })
            {
                if (Take(op))
                {
                    object right = Add();
                    return op switch
                    {
                        "==" => Equal(left, right),
                        "!=" => !Equal(left, right),
                        "<" => Num(left) < Num(right),
                        "<=" => Num(left) <= Num(right),
                        ">" => Num(left) > Num(right),
                        _ => Num(left) >= Num(right),
                    };
                }
            }

            return left;
        }

        private object Add()
        {
            object left = Mul();
            while (true)
            {
                if (Take("+"))
                {
                    object right = Mul();
                    left = left is string || right is string ? Text(left) + Text(right) : Num(left) + Num(right);
                }
                else if (Take("-"))
                {
                    left = Num(left) - Num(Mul());
                }
                else
                {
                    return left;
                }
            }
        }

        private object Mul()
        {
            object left = Unary();
            while (true)
            {
                if (Take("*"))
                {
                    left = Num(left) * Num(Unary());
                }
                else if (Take("/"))
                {
                    double d = Num(Unary());
                    left = d == 0 ? throw Error("division by zero") : Num(left) / d;
                }
                else if (Take("%"))
                {
                    double d = Num(Unary());
                    left = d == 0 ? throw Error("division by zero") : Num(left) % d;
                }
                else
                {
                    return left;
                }
            }
        }

        private object Unary()
        {
            if (Take("-"))
            {
                return -Num(Unary());
            }

            if (Peek("!") && !Peek("!="))
            {
                _pos++;
                return !Truthy(Unary());
            }

            return Primary();
        }

        private object Primary()
        {
            SkipSpace();
            if (AtEnd)
            {
                throw Error("expression ends early");
            }

            char c = text[_pos];
            if (Take("("))
            {
                object v = Or();
                return Take(")") ? v : throw Error("missing )");
            }

            if (c == '\'')
            {
                int end = text.IndexOf('\'', _pos + 1);
                if (end < 0)
                {
                    throw Error("unclosed 'text'");
                }

                string s = text[(_pos + 1)..end];
                _pos = end + 1;
                return s;
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = _pos;
                while (!AtEnd && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
                {
                    _pos++;
                }

                return double.TryParse(text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    ? n
                    : throw Error($"bad number '{text[start.._pos]}'");
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = _pos;
                while (!AtEnd && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                {
                    _pos++;
                }

                string name = text[start.._pos];
                if (Take("("))
                {
                    return Call(name);
                }

                return name switch
                {
                    "true" => true,
                    "false" => false,
                    _ => vars.TryGetValue(name, out object? v) ? v : throw Error($"unknown name '{name}' (parameters: {string.Join(", ", vars.Keys)})"),
                };
            }

            throw Error($"unexpected '{c}'");
        }

        private object Call(string name)
        {
            var args = new List<double>();
            if (!Take(")"))
            {
                do
                {
                    args.Add(Num(Or()));
                }
                while (Take(","));

                if (!Take(")"))
                {
                    throw Error($"missing ) after {name}(...");
                }
            }

            double Arg(int i) => i < args.Count ? args[i] : throw Error($"{name} needs {i + 1} argument{(i == 0 ? "" : "s")}");
            return name switch
            {
                "min" when args.Count > 0 => args.Min(),
                "max" when args.Count > 0 => args.Max(),
                "abs" => Math.Abs(Arg(0)),
                "floor" => Math.Floor(Arg(0)),
                "ceil" => Math.Ceiling(Arg(0)),
                "round" => Math.Round(Arg(0), MidpointRounding.AwayFromZero),
                "sqrt" => Math.Sqrt(Arg(0)),
                "clamp" => Math.Clamp(Arg(0), Arg(1), Math.Max(Arg(1), Arg(2))),
                _ => throw Error($"unknown function {name} (min, max, abs, floor, ceil, round, sqrt, clamp)"),
            };
        }

        private double Num(object v) => v switch
        {
            double d => d,
            bool b => b ? 1 : 0,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
            _ => throw Error($"'{Text(v)}' is not a number"),
        };

        private static bool Equal(object a, object b) =>
            a is string || b is string
                ? Text(a) == Text(b)
                : Convert.ToDouble(a, CultureInfo.InvariantCulture) == Convert.ToDouble(b, CultureInfo.InvariantCulture);

        public void SkipSpace()
        {
            while (!AtEnd && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        private bool Peek(string token)
        {
            SkipSpace();
            return string.CompareOrdinal(text, _pos, token, 0, token.Length) == 0;
        }

        private bool Take(string token)
        {
            if (!Peek(token))
            {
                return false;
            }

            // Callers try "<=" before "<", so a shorter token never eats half of a longer one.
            _pos += token.Length;
            return true;
        }
    }
}

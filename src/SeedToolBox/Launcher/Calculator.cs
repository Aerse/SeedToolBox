using System;
using System.Collections.Generic;
using System.Globalization;

namespace SeedToolBox.Launcher;

/// <summary>Evaluates arithmetic such as "2^10 + sqrt(9) * (1 - 3) % 5" with a recursive-descent parser.</summary>
public sealed class Calculator
{
    readonly string _s;
    int _pos;

    Calculator(string s) => _s = s;

    /// <summary>Returns false with an error message if the expression is incomplete or invalid.</summary>
    public static bool TryEvaluate(string expression, out double value, out string error)
    {
        value = 0;
        error = "";
        try
        {
            var calc = new Calculator(expression);
            value = calc.Expr();
            calc.SkipSpace();
            if (calc._pos < calc._s.Length) throw new FormatException($"无法识别「{calc._s[calc._pos]}」");
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new FormatException("结果不是有效数字");
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Format(double value)
    {
        if (Math.Abs(value) < 1e15 && value == Math.Round(value)) return value.ToString("0", CultureInfo.InvariantCulture);
        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    void SkipSpace()
    {
        while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
    }

    bool Eat(char c)
    {
        SkipSpace();
        if (_pos < _s.Length && _s[_pos] == c) { _pos++; return true; }
        return false;
    }

    // expr := term (('+' | '-') term)*
    double Expr()
    {
        var v = Term();
        while (true)
        {
            if (Eat('+')) v += Term();
            else if (Eat('-')) v -= Term();
            else return v;
        }
    }

    // term := unary (('*' | '/' | '%') unary)*
    double Term()
    {
        var v = Unary();
        while (true)
        {
            if (Eat('*') || Eat('×')) v *= Unary();
            else if (Eat('/') || Eat('÷')) v /= Unary();
            else if (Eat('%')) v %= Unary();
            else return v;
        }
    }

    // unary := ('-' | '+') unary | power
    double Unary()
    {
        if (Eat('-')) return -Unary();
        if (Eat('+')) return Unary();
        return Power();
    }

    // power := primary ('^' unary)?, right-associative so 2^3^2 = 2^9
    double Power()
    {
        var v = Primary();
        return Eat('^') ? Math.Pow(v, Unary()) : v;
    }

    // primary := number | name | name '(' args ')' | '(' expr ')'
    double Primary()
    {
        SkipSpace();
        if (_pos >= _s.Length) throw new FormatException("表达式不完整");
        if (Eat('('))
        {
            var v = Expr();
            if (!Eat(')')) throw new FormatException("缺少右括号");
            return v;
        }

        char c = _s[_pos];
        if (char.IsDigit(c) || c == '.') return Number();
        if (char.IsLetter(c))
        {
            int start = _pos;
            while (_pos < _s.Length && char.IsLetterOrDigit(_s[_pos])) _pos++;
            var name = _s.Substring(start, _pos - start).ToLowerInvariant();
            if (!Eat('(')) return Constant(name);
            var args = new List<double>();
            if (!Eat(')'))
            {
                do args.Add(Expr()); while (Eat(','));
                if (!Eat(')')) throw new FormatException("缺少右括号");
            }
            return Call(name, args);
        }
        throw new FormatException($"无法识别「{c}」");
    }

    double Number()
    {
        int start = _pos;
        while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.')) _pos++;
        // Exponent such as 1e-3; a bare "e" after a number is left alone
        if (_pos < _s.Length && _s[_pos] is 'e' or 'E')
        {
            int save = _pos++;
            if (_pos < _s.Length && _s[_pos] is '+' or '-') _pos++;
            if (_pos < _s.Length && char.IsDigit(_s[_pos]))
                while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
            else _pos = save;
        }
        var text = _s.Substring(start, _pos - start);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new FormatException($"无效数字「{text}」");
        return v;
    }

    static double Constant(string name) => name switch
    {
        "pi" => Math.PI,
        "e" => Math.E,
        _ => throw new FormatException($"未知常量「{name}」"),
    };

    static double Call(string name, List<double> a)
    {
        double One() => a.Count == 1 ? a[0] : throw new FormatException($"{name} 需要 1 个参数");
        return name switch
        {
            "sqrt" => Math.Sqrt(One()),
            "sin" => Math.Sin(One()),
            "cos" => Math.Cos(One()),
            "tan" => Math.Tan(One()),
            "asin" => Math.Asin(One()),
            "acos" => Math.Acos(One()),
            "atan" => Math.Atan(One()),
            "log" => a.Count == 2 ? Math.Log(a[0], a[1]) : Math.Log10(One()),
            "ln" => Math.Log(One()),
            "exp" => Math.Exp(One()),
            "abs" => Math.Abs(One()),
            "floor" => Math.Floor(One()),
            "ceil" => Math.Ceiling(One()),
            "round" => Math.Round(One(), MidpointRounding.AwayFromZero),
            "pow" => a.Count == 2 ? Math.Pow(a[0], a[1]) : throw new FormatException("pow 需要 2 个参数"),
            "min" when a.Count > 0 => Fold(a, Math.Min),
            "max" when a.Count > 0 => Fold(a, Math.Max),
            _ => throw new FormatException($"未知函数「{name}」"),
        };
    }

    static double Fold(List<double> a, Func<double, double, double> f)
    {
        var m = a[0];
        foreach (var x in a) m = f(m, x);
        return m;
    }
}

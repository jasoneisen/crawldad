using System.Globalization;

namespace Crawldad.Api.Features.Runs.Interpreter.Expressions;

/// <summary>The lexical categories of the expression grammar.</summary>
internal enum TokenType
{
    Number, String, Identifier, True, False, Null,
    Plus, Minus, Star, Slash, Percent,
    EqEq, NotEq, Lt, LtEq, Gt, GtEq,
    AmpAmp, PipePipe, Bang,
    Question, Colon,
    Dot, LBracket, RBracket, LParen, RParen, LBrace, RBrace, Comma,
    End,
}

/// <summary>One lexeme: its <see cref="Type"/>, the source <see cref="Position"/> it started at, plus the literal
/// <see cref="Value"/> (number/string/bool/null) or identifier <see cref="Text"/> where relevant.</summary>
internal readonly record struct Token(TokenType Type, int Position, string Text = "", object? Value = null);

/// <summary>Turns an expression source string into a flat token list ending in <see cref="TokenType.End"/>. Pure and
/// total; malformed input is a terminal <see cref="ExpressionParseException"/> (<c>syntax_error</c>) with the
/// offending position. Single-quoted strings support <c>\n \t \r \\ \'</c> escapes.</summary>
internal static class Lexer
{
    public static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var pos = 0;

        while (pos < source.Length)
        {
            var c = source[pos];

            if (char.IsWhiteSpace(c))
            {
                pos++;
                continue;
            }

            if (char.IsDigit(c))
            {
                tokens.Add(LexNumber(source, ref pos));
                continue;
            }

            if (c == '_' || char.IsLetter(c))
            {
                tokens.Add(LexIdentifier(source, ref pos));
                continue;
            }

            if (c == '\'')
            {
                tokens.Add(LexString(source, ref pos));
                continue;
            }

            tokens.Add(LexOperator(source, ref pos));
        }

        tokens.Add(new Token(TokenType.End, pos));
        return tokens;
    }

    private static Token LexNumber(string source, ref int pos)
    {
        var start = pos;
        while (pos < source.Length && char.IsDigit(source[pos]))
        {
            pos++;
        }

        var isDouble = false;
        if (pos + 1 < source.Length && source[pos] == '.' && char.IsDigit(source[pos + 1]))
        {
            isDouble = true;
            pos++; // consume '.'
            while (pos < source.Length && char.IsDigit(source[pos]))
            {
                pos++;
            }
        }

        var text = source[start..pos];
        if (isDouble)
        {
            return new Token(TokenType.Number, start, Value: double.Parse(text, CultureInfo.InvariantCulture));
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw Syntax($"integer literal '{text}' is out of range", start);
        }

        return new Token(TokenType.Number, start, Value: value);
    }

    private static Token LexIdentifier(string source, ref int pos)
    {
        var start = pos;
        while (pos < source.Length && (source[pos] == '_' || char.IsLetterOrDigit(source[pos])))
        {
            pos++;
        }

        var text = source[start..pos];
        return text switch
        {
            "true" => new Token(TokenType.True, start, Value: true),
            "false" => new Token(TokenType.False, start, Value: false),
            "null" => new Token(TokenType.Null, start),
            _ => new Token(TokenType.Identifier, start, Text: text),
        };
    }

    private static Token LexString(string source, ref int pos)
    {
        var start = pos;
        pos++; // opening quote
        var sb = new System.Text.StringBuilder();

        while (pos < source.Length && source[pos] != '\'')
        {
            var c = source[pos];
            if (c == '\\')
            {
                pos++;
                if (pos >= source.Length)
                {
                    break;
                }

                sb.Append(source[pos] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '\'' => '\'',
                    _ => throw Syntax($"invalid string escape '\\{source[pos]}'", pos - 1),
                });
                pos++;
                continue;
            }

            sb.Append(c);
            pos++;
        }

        if (pos >= source.Length)
        {
            throw Syntax("unterminated string literal", start);
        }

        pos++; // closing quote
        return new Token(TokenType.String, start, Value: sb.ToString());
    }

    private static Token LexOperator(string source, ref int pos)
    {
        var start = pos;
        var c = source[pos];
        switch (c)
        {
            case '+': pos++; return new Token(TokenType.Plus, start);
            case '-': pos++; return new Token(TokenType.Minus, start);
            case '*': pos++; return new Token(TokenType.Star, start);
            case '/': pos++; return new Token(TokenType.Slash, start);
            case '%': pos++; return new Token(TokenType.Percent, start);
            case '?': pos++; return new Token(TokenType.Question, start);
            case ':': pos++; return new Token(TokenType.Colon, start);
            case '.': pos++; return new Token(TokenType.Dot, start);
            case '[': pos++; return new Token(TokenType.LBracket, start);
            case ']': pos++; return new Token(TokenType.RBracket, start);
            case '(': pos++; return new Token(TokenType.LParen, start);
            case ')': pos++; return new Token(TokenType.RParen, start);
            case '{': pos++; return new Token(TokenType.LBrace, start);
            case '}': pos++; return new Token(TokenType.RBrace, start);
            case ',': pos++; return new Token(TokenType.Comma, start);
            case '<': return TwoOrOne(source, ref pos, '=', TokenType.LtEq, TokenType.Lt);
            case '>': return TwoOrOne(source, ref pos, '=', TokenType.GtEq, TokenType.Gt);
            case '!': return TwoOrOne(source, ref pos, '=', TokenType.NotEq, TokenType.Bang);
            case '=': return TwoRequired(source, ref pos, '=', TokenType.EqEq);
            case '&': return TwoRequired(source, ref pos, '&', TokenType.AmpAmp);
            case '|': return TwoRequired(source, ref pos, '|', TokenType.PipePipe);
            default: throw Syntax($"unexpected character '{c}'", start);
        }
    }

    private static Token TwoOrOne(string source, ref int pos, char second, TokenType two, TokenType one)
    {
        var start = pos;
        pos++;
        if (pos < source.Length && source[pos] == second)
        {
            pos++;
            return new Token(two, start);
        }

        return new Token(one, start);
    }

    private static Token TwoRequired(string source, ref int pos, char second, TokenType two)
    {
        var start = pos;
        pos++;
        if (pos < source.Length && source[pos] == second)
        {
            pos++;
            return new Token(two, start);
        }

        throw Syntax($"expected '{second}' after '{source[start]}'", start);
    }

    private static ExpressionParseException Syntax(string message, int position) =>
        new(ExpressionErrorCodes.SyntaxError, message, position);
}

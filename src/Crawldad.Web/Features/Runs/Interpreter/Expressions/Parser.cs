namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>A recursive-descent parser for the expression grammar, turning a token list into an
/// <see cref="ExpressionNode"/> tree. Pure and static — a validation pass with no side effects. Builtin names/arities
/// are checked here (<c>unknown_function</c>/<c>wrong_arity</c>); nesting is capped at <see cref="MaxDepth"/>.</summary>
internal sealed class Parser
{
    /// <summary>Maximum nesting of parenthesised/collection/unary sub-expressions before a terminal parse failure.</summary>
    private static int MaxDepth => 64;

    private readonly List<Token> _tokens;
    private int _pos;
    private int _depth;

    public Parser(List<Token> tokens) => _tokens = tokens;

    /// <summary>Parses a complete expression and asserts the whole input was consumed.</summary>
    public ExpressionNode ParseProgram()
    {
        var node = ParseExpression();
        if (Peek.Type != TokenType.End)
        {
            throw Syntax($"unexpected trailing token '{Describe(Peek)}'", Peek.Position);
        }

        return node;
    }

    private Token Peek => _tokens[_pos];

    private Token Advance() => _tokens[_pos++];

    private bool Check(TokenType type) => Peek.Type == type;

    private bool Match(TokenType type)
    {
        if (Check(type))
        {
            _pos++;
            return true;
        }

        return false;
    }

    private Token Expect(TokenType type, string message)
    {
        if (Check(type))
        {
            return Advance();
        }

        throw Syntax(message, Peek.Position);
    }

    // ternary (lowest precedence) — the shared recursion point for every nested sub-expression.
    private ExpressionNode ParseExpression()
    {
        if (++_depth > MaxDepth)
        {
            throw new ExpressionParseException(
                ExpressionErrorCodes.ExpressionTooDeep, $"expression nesting exceeds the limit of {MaxDepth}", Peek.Position);
        }

        try
        {
            var condition = ParseOr();
            if (Match(TokenType.Question))
            {
                var ifTrue = ParseExpression();
                Expect(TokenType.Colon, "expected ':' in ternary expression");
                var ifFalse = ParseExpression();
                return new TernaryNode(condition, ifTrue, ifFalse);
            }

            return condition;
        }
        finally
        {
            _depth--;
        }
    }

    private ExpressionNode ParseOr()
    {
        var left = ParseAnd();
        while (Match(TokenType.PipePipe))
        {
            left = new OrNode(left, ParseAnd());
        }

        return left;
    }

    private ExpressionNode ParseAnd()
    {
        var left = ParseEquality();
        while (Match(TokenType.AmpAmp))
        {
            left = new AndNode(left, ParseEquality());
        }

        return left;
    }

    private ExpressionNode ParseEquality()
    {
        var left = ParseRelational();
        while (Check(TokenType.EqEq) || Check(TokenType.NotEq))
        {
            var isEqual = Check(TokenType.EqEq);
            Advance();
            var right = ParseRelational();
            left = new BinaryNode(left, right,
                isEqual
                    ? static (l, r) => ExpressionValues.AreEqual(l, r)
                    : static (l, r) => !ExpressionValues.AreEqual(l, r));
        }

        return left;
    }

    private ExpressionNode ParseRelational()
    {
        var left = ParseAdditive();
        while (true)
        {
            Func<object?, object?, object?> op;
            if (Check(TokenType.Lt))
            {
                op = static (l, r) => ExpressionValues.Less(l, r);
            }
            else if (Check(TokenType.LtEq))
            {
                op = static (l, r) => ExpressionValues.LessOrEqual(l, r);
            }
            else if (Check(TokenType.Gt))
            {
                op = static (l, r) => ExpressionValues.Greater(l, r);
            }
            else if (Check(TokenType.GtEq))
            {
                op = static (l, r) => ExpressionValues.GreaterOrEqual(l, r);
            }
            else
            {
                return left;
            }

            Advance();
            left = new BinaryNode(left, ParseAdditive(), op);
        }
    }

    private ExpressionNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenType.Plus) || Check(TokenType.Minus))
        {
            var isPlus = Check(TokenType.Plus);
            Advance();
            var right = ParseMultiplicative();
            left = new BinaryNode(left, right, isPlus ? ExpressionValues.Add : ExpressionValues.Subtract);
        }

        return left;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            Func<object?, object?, object?> op;
            if (Check(TokenType.Star))
            {
                op = ExpressionValues.Multiply;
            }
            else if (Check(TokenType.Slash))
            {
                op = ExpressionValues.Divide;
            }
            else if (Check(TokenType.Percent))
            {
                op = ExpressionValues.Modulo;
            }
            else
            {
                return left;
            }

            Advance();
            left = new BinaryNode(left, ParseUnary(), op);
        }
    }

    private ExpressionNode ParseUnary()
    {
        if (++_depth > MaxDepth)
        {
            throw new ExpressionParseException(
                ExpressionErrorCodes.ExpressionTooDeep, $"expression nesting exceeds the limit of {MaxDepth}", Peek.Position);
        }

        try
        {
            if (Match(TokenType.Bang))
            {
                return new NotNode(ParseUnary());
            }

            if (Match(TokenType.Minus))
            {
                return new NegateNode(ParseUnary());
            }

            return ParsePostfix();
        }
        finally
        {
            _depth--;
        }
    }

    private ExpressionNode ParsePostfix()
    {
        var node = ParsePrimary();
        while (true)
        {
            if (Match(TokenType.Dot))
            {
                var name = Expect(TokenType.Identifier, "expected a member name after '.'");
                node = new MemberNode(node, name.Text);
            }
            else if (Match(TokenType.LBracket))
            {
                var index = ParseExpression();
                Expect(TokenType.RBracket, "expected ']' to close an index");
                node = new IndexNode(node, index);
            }
            else
            {
                return node;
            }
        }
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Peek;
        switch (token.Type)
        {
            case TokenType.Number:
            case TokenType.String:
            case TokenType.True:
            case TokenType.False:
            case TokenType.Null:
                Advance();
                return new LiteralNode(token.Value);

            case TokenType.Identifier:
                Advance();
                return Check(TokenType.LParen) ? ParseCall(token) : new IdentifierNode(token.Text);

            case TokenType.LParen:
                Advance();
                var inner = ParseExpression();
                Expect(TokenType.RParen, "expected ')' to close a parenthesised expression");
                return inner;

            case TokenType.LBracket:
                return ParseArray();

            case TokenType.LBrace:
                return ParseObject();

            default:
                throw Syntax($"unexpected token '{Describe(token)}'", token.Position);
        }
    }

    private ExpressionNode ParseCall(Token name)
    {
        // A name is in exactly one registry (binding names are disjoint from ordinary ones); unknown in both is the
        // static safety-boundary rejection (eval/require/fs/… never resolve here).
        var isBinding = BuiltinRegistry.TryGetBinding(name.Text, out var bindingBuiltin);
        var isOrdinary = BuiltinRegistry.TryGet(name.Text, out var builtin);
        if (!isBinding && !isOrdinary)
        {
            throw new ExpressionParseException(
                ExpressionErrorCodes.UnknownFunction, $"unknown function '{name.Text}'", name.Position);
        }

        Advance(); // '('
        var args = new List<ExpressionNode>();
        if (!Check(TokenType.RParen))
        {
            do
            {
                args.Add(ParseExpression());
            }
            while (Match(TokenType.Comma));
        }

        Expect(TokenType.RParen, $"expected ')' to close the call to '{name.Text}'");

        return isBinding ? BuildBindingCall(name, bindingBuiltin, args) : BuildCall(name, builtin, args);
    }

    private static CallNode BuildCall(Token name, Builtin builtin, List<ExpressionNode> args)
    {
        if (args.Count < builtin.MinArity || args.Count > builtin.MaxArity)
        {
            throw new ExpressionParseException(
                ExpressionErrorCodes.WrongArity, DescribeArity(builtin, args.Count), name.Position);
        }

        return new CallNode(builtin.Invoke, args);
    }

    // Binding builtins are the fixed fn(source, v, body) form: exactly three arguments, the middle one a bare binding
    // identifier that names the per-element variable. A non-identifier binding slot (e.g. filter(xs, 1+1, …)) is a
    // syntax error — no expression can smuggle a computation into the binding position.
    private static BindingCallNode BuildBindingCall(Token name, BindingBuiltin builtin, List<ExpressionNode> args)
    {
        if (args.Count != 3)
        {
            throw new ExpressionParseException(
                ExpressionErrorCodes.WrongArity, $"'{name.Text}' expects 3 argument(s) but got {args.Count}", name.Position);
        }

        if (args[1] is not IdentifierNode identifier)
        {
            throw new ExpressionParseException(
                ExpressionErrorCodes.SyntaxError,
                $"the second argument to '{name.Text}' must be a bare binding identifier", name.Position);
        }

        return new BindingCallNode(builtin.Invoke, args[0], identifier.Name, args[2]);
    }

    private ArrayNode ParseArray()
    {
        Advance(); // '['
        var items = new List<ExpressionNode>();
        if (!Check(TokenType.RBracket))
        {
            do
            {
                items.Add(ParseExpression());
            }
            while (Match(TokenType.Comma));
        }

        Expect(TokenType.RBracket, "expected ']' to close an array literal");
        return new ArrayNode(items);
    }

    private ObjectNode ParseObject()
    {
        Advance(); // '{'
        var entries = new List<KeyValuePair<string, ExpressionNode>>();
        if (!Check(TokenType.RBrace))
        {
            do
            {
                var key = ParseObjectKey();
                Expect(TokenType.Colon, "expected ':' after an object key");
                entries.Add(new KeyValuePair<string, ExpressionNode>(key, ParseExpression()));
            }
            while (Match(TokenType.Comma));
        }

        Expect(TokenType.RBrace, "expected '}' to close an object literal");
        return new ObjectNode(entries);
    }

    private string ParseObjectKey()
    {
        var token = Peek;
        if (token.Type == TokenType.Identifier)
        {
            Advance();
            return token.Text;
        }

        if (token.Type == TokenType.String)
        {
            Advance();
            return (string)token.Value!;
        }

        throw Syntax("expected an object key (identifier or quoted string)", token.Position);
    }

    private static string DescribeArity(Builtin builtin, int actual)
    {
        var expected = builtin.MinArity == builtin.MaxArity
            ? $"{builtin.MinArity}"
            : builtin.MaxArity == int.MaxValue
                ? $"at least {builtin.MinArity}"
                : $"{builtin.MinArity} to {builtin.MaxArity}";
        return $"'{builtin.Name}' expects {expected} argument(s) but got {actual}";
    }

    private static string Describe(Token token) =>
        token.Type switch
        {
            TokenType.End => "<end of input>",
            TokenType.Identifier => token.Text,
            TokenType.String => "<string>",
            TokenType.Number => "<number>",
            _ => token.Type.ToString(),
        };

    private static ExpressionParseException Syntax(string message, int position) =>
        new(ExpressionErrorCodes.SyntaxError, message, position);
}

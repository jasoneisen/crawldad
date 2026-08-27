using Crawldad.Portal.Auth;

namespace Crawldad.Tests.Portal;

public class OtpCodeGeneratorTests
{
    private readonly OtpCodeGenerator _generator = new();

    [Fact]
    public void Generates_six_character_codes() =>
        _generator.Generate().Length.ShouldBe(OtpCodeGenerator.CodeLength);

    [Fact]
    public void Uses_only_alphabet_characters()
    {
        for (var i = 0; i < 2000; i++)
        {
            foreach (var ch in _generator.Generate())
            {
                OtpCodeGenerator.Alphabet.Contains(ch, StringComparison.Ordinal).ShouldBeTrue();
            }
        }
    }

    [Fact]
    public void Alphabet_excludes_visually_confusable_characters()
    {
        foreach (var confusable in "0O1IL")
        {
            OtpCodeGenerator.Alphabet.Contains(confusable, StringComparison.Ordinal).ShouldBeFalse();
        }
    }

    [Fact]
    public void Alphabet_is_31_distinct_symbols()
    {
        OtpCodeGenerator.Alphabet.Length.ShouldBe(31);
        OtpCodeGenerator.Alphabet.Distinct().Count().ShouldBe(31);
    }

    [Fact]
    public void Draws_the_whole_alphabet_and_is_high_entropy()
    {
        var seenChars = new HashSet<char>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 5000; i++)
        {
            var code = _generator.Generate();
            codes.Add(code);
            foreach (var ch in code)
            {
                seenChars.Add(ch);
            }
        }

        // Every alphabet symbol turns up across ~30k draws (unbiased spread) and codes are essentially all unique
        // (no low-entropy/constant output).
        seenChars.Count.ShouldBe(OtpCodeGenerator.Alphabet.Length);
        codes.Count.ShouldBeGreaterThan(4990);
    }
}

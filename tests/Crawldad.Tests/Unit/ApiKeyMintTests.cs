using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The registry key mint: a key is <c>ck_&lt;env&gt;_&lt;random&gt;</c>, only its SHA-256 is stored, and each
/// mint is fresh and high-entropy. (The raw keys here are randomly minted test values — never real credentials.)</summary>
public class ApiKeyMintTests
{
    [Fact]
    public void Mints_a_key_in_the_ck_env_format_with_a_display_prefix()
    {
        var minted = ApiKeyMint.Issue("staging");

        minted.Raw.ShouldStartWith("ck_staging_");
        minted.Prefix.ShouldStartWith("ck_staging_");
        minted.Raw.ShouldStartWith(minted.Prefix); // the prefix is a true, non-secret leading slice of the key
        minted.Prefix.Length.ShouldBe("ck_staging_".Length + ApiKeyMint.PrefixRandomChars);
        minted.Raw.Length.ShouldBeGreaterThan(minted.Prefix.Length); // the secret tail is far longer than the shown prefix
    }

    [Fact]
    public void Stores_only_the_sha256_hash_of_the_raw_key()
    {
        var minted = ApiKeyMint.Issue("dev");

        minted.Hash.ShouldBe(ApiKeyMint.Hash(minted.Raw)); // the stored hash is exactly SHA-256(raw)
        minted.Hash.ShouldNotContain("ck_"); // the hash carries none of the raw key
        minted.Hash.ShouldMatch("^[0-9a-f]{64}$"); // 256-bit digest, lowercase hex
    }

    [Fact]
    public void Mints_a_distinct_high_entropy_key_each_time()
    {
        var a = ApiKeyMint.Issue("dev");
        var b = ApiKeyMint.Issue("dev");

        a.Raw.ShouldNotBe(b.Raw);
        a.Hash.ShouldNotBe(b.Hash);
        a.Prefix.ShouldNotBe(b.Prefix);
    }

    [Fact]
    public void Hashing_is_deterministic()
    {
        const string fake = "ck_dev_this-is-a-synthetic-test-key";
        ApiKeyMint.Hash(fake).ShouldBe(ApiKeyMint.Hash(fake));
    }

    [Fact]
    public void Rejects_a_blank_env_label() =>
        Should.Throw<ArgumentException>(() => ApiKeyMint.Issue("   "));
}

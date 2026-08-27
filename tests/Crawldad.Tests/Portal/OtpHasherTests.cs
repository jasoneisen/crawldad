using Crawldad.Portal.Auth;

namespace Crawldad.Tests.Portal;

public class OtpHasherTests
{
    [Fact]
    public void Verifies_the_correct_code()
    {
        var (hash, salt) = OtpHasher.Hash("ABC234");

        OtpHasher.Verify("ABC234", salt, hash).ShouldBeTrue();
    }

    [Fact]
    public void Rejects_a_wrong_code()
    {
        var (hash, salt) = OtpHasher.Hash("ABC234");

        OtpHasher.Verify("XYZ789", salt, hash).ShouldBeFalse();
    }

    [Fact]
    public void Uses_a_fresh_salt_so_the_same_code_hashes_differently()
    {
        var (hash1, salt1) = OtpHasher.Hash("ABC234");
        var (hash2, salt2) = OtpHasher.Hash("ABC234");

        salt1.ShouldNotBe(salt2);
        hash1.ShouldNotBe(hash2);
    }

    [Fact]
    public void Constant_time_compare_rejects_a_different_length_hash_without_throwing()
    {
        var (_, salt) = OtpHasher.Hash("ABC234");
        var wrongLengthHash = Convert.ToBase64String(new byte[8]);

        OtpHasher.Verify("ABC234", salt, wrongLengthHash).ShouldBeFalse();
    }
}

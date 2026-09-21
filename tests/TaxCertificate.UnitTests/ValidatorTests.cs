using TaxCertificate.Application.Validators;
using Xunit;

namespace TaxCertificate.UnitTests;

public class VergiKimlikNoValidatorTests
{
    // 4540536920 is the reference-valid number from python-stdnum's stdnum.tr.vkn doctests.
    [Theory]
    [InlineData("4540536920")]
    [InlineData("1234567890")]
    [InlineData("0123456789")]
    public void IsValid_returns_true_for_checksum_valid_numbers(string value)
        => Assert.True(VergiKimlikNoValidator.IsValid(value));

    [Theory]
    [InlineData("4540536921")]  // reference invalid: last digit off by one
    [InlineData("8790021147")]
    [InlineData("1234567891")]
    public void IsValid_returns_false_for_checksum_invalid_numbers(string value)
        => Assert.False(VergiKimlikNoValidator.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("454053692")]      // 9 digits
    [InlineData("45405369201")]    // 11 digits
    [InlineData("454053692A")]     // non-digit
    [InlineData("0000000000")]     // arithmetically valid but never a real VKN
    public void IsValid_returns_false_for_malformed_input(string? value)
        => Assert.False(VergiKimlikNoValidator.IsValid(value));

    [Fact]
    public void CalculateCheckDigit_reproduces_the_reference_digit()
        => Assert.Equal(0, VergiKimlikNoValidator.CalculateCheckDigit("454053692"));

    [Fact]
    public void CalculateCheckDigit_round_trips_for_every_generated_number()
    {
        // Self-consistency sweep: appending the computed digit must always validate.
        for (var seed = 0; seed < 500; seed++)
        {
            var body = (100000000 + seed * 1777).ToString()[..9];
            var full = body + VergiKimlikNoValidator.CalculateCheckDigit(body);
            Assert.True(VergiKimlikNoValidator.IsValid(full), $"failed for {full}");
        }
    }
}

public class TcKimlikNoValidatorTests
{
    // 17291716060 is the reference-valid number from python-stdnum's stdnum.tr.tckimlik doctests.
    [Theory]
    [InlineData("17291716060")]
    [InlineData("10000000146")]
    [InlineData("11111111110")]
    public void IsValid_returns_true_for_checksum_valid_numbers(string value)
        => Assert.True(TcKimlikNoValidator.IsValid(value));

    [Theory]
    [InlineData("17291716050")]  // reference invalid: wrong 10th digit
    [InlineData("12345678901")]
    [InlineData("11111111111")]
    public void IsValid_returns_false_for_checksum_invalid_numbers(string value)
        => Assert.False(TcKimlikNoValidator.IsValid(value));

    [Fact]
    public void IsValid_rejects_a_leading_zero_even_when_the_checksum_passes()
    {
        // 07291716092 satisfies both check digits but a TCKN never starts with 0.
        var (tenth, eleventh) = TcKimlikNoValidator.CalculateCheckDigits("072917160");
        Assert.Equal(9, tenth);
        Assert.Equal(2, eleventh);
        Assert.False(TcKimlikNoValidator.IsValid("07291716092"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1729171606")]      // 10 digits
    [InlineData("172917160600")]    // 12 digits
    [InlineData("1729171606A")]
    public void IsValid_returns_false_for_malformed_input(string? value)
        => Assert.False(TcKimlikNoValidator.IsValid(value));

    [Fact]
    public void A_vkn_is_never_accepted_as_a_tckn()
    {
        Assert.True(VergiKimlikNoValidator.IsValid("4540536920"));
        Assert.False(TcKimlikNoValidator.IsValid("4540536920"));
    }
}

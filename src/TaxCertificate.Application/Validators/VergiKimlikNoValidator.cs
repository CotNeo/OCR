namespace TaxCertificate.Application.Validators;

/// <summary>
/// Turkish VKN (Vergi Kimlik Numarası) check-digit validation.
/// <para>
/// For each of the first nine digits at position <c>i</c> (0-based):
/// <c>t = (d[i] + 9 - i) mod 10</c>; when <c>t != 0</c> it contributes
/// <c>(t * 2^(9-i)) mod 9</c>, with a zero result mapped to 9.
/// The check digit is <c>(10 - sum) mod 10</c>.
/// </para>
/// <para>
/// Cross-checked against the reference implementation in python-stdnum (<c>stdnum.tr.vkn</c>):
/// 4540536920 is valid, 4540536921 is not.
/// </para>
/// </summary>
public static class VergiKimlikNoValidator
{
    public const int Length = 10;

    public static bool IsValid(string? value)
    {
        if (!HasValidShape(value))
        {
            return false;
        }

        return CalculateCheckDigit(value!) == value![Length - 1] - '0';
    }

    /// <summary>Length and digit-only check, without the checksum.</summary>
    public static bool HasValidShape(string? value)
    {
        if (value is null || value.Length != Length)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        // An all-zero number passes the checksum arithmetically but is never a real VKN.
        return value.Any(c => c != '0');
    }

    /// <summary>Computes the 10th digit from the first nine.</summary>
    public static int CalculateCheckDigit(string firstNineDigits)
    {
        ArgumentNullException.ThrowIfNull(firstNineDigits);
        if (firstNineDigits.Length < 9)
        {
            throw new ArgumentException("At least 9 digits are required.", nameof(firstNineDigits));
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var digit = firstNineDigits[i] - '0';
            if (digit is < 0 or > 9)
            {
                throw new ArgumentException("Value must contain digits only.", nameof(firstNineDigits));
            }

            var t = (digit + 9 - i) % 10;
            if (t == 0)
            {
                continue;
            }

            var contribution = (t * (1 << (9 - i))) % 9;
            sum += contribution == 0 ? 9 : contribution;
        }

        return (10 - (sum % 10)) % 10;
    }
}

namespace TaxCertificate.Application.Validators;

/// <summary>
/// Turkish TCKN (T.C. Kimlik Numarası) check-digit validation.
/// <para>
/// 11 digits, first digit non-zero.
/// <c>d10 = ((sum of digits 1,3,5,7,9) * 7 - (sum of digits 2,4,6,8)) mod 10</c> and
/// <c>d11 = (sum of the first ten digits) mod 10</c>.
/// </para>
/// <para>
/// Cross-checked against python-stdnum (<c>stdnum.tr.tckimlik</c>): 17291716060 is valid,
/// 17291716050 is not, and 07291716092 is rejected for the leading zero.
/// </para>
/// </summary>
public static class TcKimlikNoValidator
{
    public const int Length = 11;

    public static bool IsValid(string? value)
    {
        if (!HasValidShape(value))
        {
            return false;
        }

        var (tenth, eleventh) = CalculateCheckDigits(value!);
        return tenth == value![9] - '0' && eleventh == value[10] - '0';
    }

    /// <summary>Length, digit-only and leading-zero check, without the checksum.</summary>
    public static bool HasValidShape(string? value)
    {
        if (value is null || value.Length != Length || value[0] == '0')
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

        return true;
    }

    /// <summary>Computes the 10th and 11th digits from the first nine.</summary>
    public static (int Tenth, int Eleventh) CalculateCheckDigits(string firstNineDigits)
    {
        ArgumentNullException.ThrowIfNull(firstNineDigits);
        if (firstNineDigits.Length < 9)
        {
            throw new ArgumentException("At least 9 digits are required.", nameof(firstNineDigits));
        }

        int oddSum = 0, evenSum = 0, total = 0;
        for (var i = 0; i < 9; i++)
        {
            var digit = firstNineDigits[i] - '0';
            if (digit is < 0 or > 9)
            {
                throw new ArgumentException("Value must contain digits only.", nameof(firstNineDigits));
            }

            total += digit;
            if (i % 2 == 0)
            {
                oddSum += digit;
            }
            else
            {
                evenSum += digit;
            }
        }

        var tenth = (((oddSum * 7) - evenSum) % 10 + 10) % 10;
        var eleventh = (total + tenth) % 10;
        return (tenth, eleventh);
    }
}

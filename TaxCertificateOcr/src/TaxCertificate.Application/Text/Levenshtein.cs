namespace TaxCertificate.Application.Text;

/// <summary>
/// Two-row Levenshtein distance with an early-exit band, plus a normalized similarity score.
/// Kept deliberately small: fuzzy matching here only has to absorb one or two OCR slips.
/// </summary>
public static class Levenshtein
{
    public static int Distance(string a, string b, int maxDistance = int.MaxValue)
    {
        if (a == b)
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        // A length gap alone already exceeds the budget: no need to fill the matrix.
        if (Math.Abs(a.Length - b.Length) > maxDistance)
        {
            return maxDistance + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var substitutionCost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }

            if (rowMinimum > maxDistance)
            {
                return maxDistance + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>Similarity in [0,1]: 1 means identical, 0 means nothing in common.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1.0;
        }

        var longest = Math.Max(a.Length, b.Length);
        if (longest == 0)
        {
            return 1.0;
        }

        return 1.0 - (double)Distance(a, b) / longest;
    }
}

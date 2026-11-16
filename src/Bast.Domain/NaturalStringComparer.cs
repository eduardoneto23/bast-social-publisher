namespace Bast.Domain;

/// <summary>Ordenação numérica de nomes: "Video 2" vem antes de "Video 10".</summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var a = ReadDigits(x, ref i);
                var b = ReadDigits(y, ref j);
                var cmp = a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.CompareTo(b, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }

    /// <summary>Primeiro número presente no nome, ou nulo se não houver.</summary>
    public static long? FirstNumber(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i])) continue;
            var digits = ReadDigits(name, ref i);
            return digits.Length <= 18 && long.TryParse(digits, out var value) ? value : null;
        }

        return null;
    }

    private static ReadOnlySpan<char> ReadDigits(string s, ref int index)
    {
        var start = index;
        while (index < s.Length && char.IsAsciiDigit(s[index])) index++;
        var span = s.AsSpan(start, index - start).TrimStart('0');
        return span;
    }
}

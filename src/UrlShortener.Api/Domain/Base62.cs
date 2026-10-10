namespace UrlShortener.Api.Domain;

/// <summary>Base62 encoding for compact, URL-safe short codes.</summary>
public static class Base62
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Encode(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");
        if (value == 0) return "0";

        Span<char> buffer = stackalloc char[12];
        var i = buffer.Length;
        while (value > 0)
        {
            buffer[--i] = Alphabet[(int)(value % 62)];
            value /= 62;
        }
        return new string(buffer[i..]);
    }

    public static long Decode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        long result = 0;
        foreach (var c in text)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0) throw new FormatException($"Invalid Base62 character '{c}'.");
            result = checked(result * 62 + digit);
        }
        return result;
    }
}

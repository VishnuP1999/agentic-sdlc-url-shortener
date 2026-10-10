using System.Security.Cryptography;

namespace UrlShortener.Api.Domain;

/// <summary>Short code generation. NewRandom uses a CSPRNG; FromId is kept for compatibility.</summary>
public static class CodeGenerator
{
    private const long Mask = (1L << 41) - 1;
    private const long OddMultiplier = 0x1F3A5B7C9D1L; // odd => invertible mod 2^41
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int CodeLength = 7;

    public static string NewRandom()
    {
        return string.Create(CodeLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
    }

    public static string FromId(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "Id must be positive.");
        var scrambled = unchecked(id * OddMultiplier) & Mask;
        return Base62.Encode(scrambled);
    }
}

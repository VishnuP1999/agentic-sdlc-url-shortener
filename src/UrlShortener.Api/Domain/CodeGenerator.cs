namespace UrlShortener.Api.Domain;

/// <summary>
/// Turns a sequential id into a short code. Multiplying by an odd constant modulo 2^41 is a
/// bijection, so distinct ids can never collide, while codes stop being trivially enumerable.
/// 2^41 &lt; 62^7, so codes are at most 7 characters long.
/// </summary>
public static class CodeGenerator
{
    private const long Mask = (1L << 41) - 1;
    private const long OddMultiplier = 0x1F3A5B7C9D1L; // odd => invertible mod 2^41

    public static string FromId(long id)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id), "Id must be positive.");
        var scrambled = unchecked(id * OddMultiplier) & Mask;
        return Base62.Encode(scrambled);
    }
}

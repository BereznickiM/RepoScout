using System.Text;

namespace RepoScout.Api.Agent;

public static class UntrustedText
{
    // Removes invisible characters commonly used to hide instructions: zero-width chars and the Unicode Tags block.
    public static string Clean(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x200B and <= 0x200D or 0x2060 or 0xFEFF or >= 0xE0000 and <= 0xE007F)
            {
                continue;
            }

            result.Append(rune);
        }

        return result.ToString();
    }
}

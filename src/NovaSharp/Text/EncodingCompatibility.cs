using System.Collections.Frozen;
using System.Text;

namespace NovaSharp.Text;

/// <summary>
/// Works out which encodings can write a document, cheaply enough to do it for the whole catalogue at once.
/// </summary>
/// <remarks>
/// Testing every encoding against the whole document would be the catalogue's size multiplied by the file's, which is
/// far too slow to do while a menu is opening. Whether an encoding can represent a document depends only on which
/// characters appear in it, not on how often or in what order, so the document is first reduced to its distinct
/// characters — a few hundred at most for real source — and every encoding is tested against that instead.
/// </remarks>
public static class EncodingCompatibility
{
    /// <summary>Returns a string containing each distinct character of <paramref name="text"/> exactly once.</summary>
    public static string Reduce(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var seen = new HashSet<int>();
        var builder = new StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            if (seen.Add(rune.Value))
            {
                builder.Append(rune);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns, for each profile, whether it can write a document containing <paramref name="text"/>'s characters.
    /// </summary>
    /// <param name="profiles">The encodings to test.</param>
    /// <param name="text">A document, or the reduction of one from <see cref="Reduce"/>.</param>
    public static FrozenDictionary<string, bool> Evaluate(IEnumerable<TextEncodingProfile> profiles, string text)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(text);

        var reduced = Reduce(text);
        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            results[profile.Id] = profile.CanRepresent(reduced);
        }

        return results.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}

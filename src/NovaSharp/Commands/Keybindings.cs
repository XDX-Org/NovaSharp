using System.Collections.Frozen;

namespace NovaSharp.Commands;

/// <summary>
/// Parses and normalizes the keybinding strings a command declares.
/// </summary>
/// <remarks>
/// The normalized form is deliberately Monaco's own vocabulary — <c>CtrlCmd</c>, <c>Shift</c>, <c>KeyS</c>, <c>F5</c>
/// — so the editor host resolves a binding by looking each token up rather than by knowing a grammar. Two parsers for
/// one syntax is how a binding ends up silently doing nothing on one platform.
///
/// <c>CtrlCmd</c> is the whole point of using Monaco's vocabulary: it is the command key on macOS and control
/// everywhere else, resolved by Monaco. Naming a platform here instead would put an operating-system branch in
/// product code.
/// </remarks>
public static class Keybindings
{
    private static readonly FrozenSet<string> Modifiers =
        new[] { "CtrlCmd", "Shift", "Alt", "WinCtrl" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> NamedKeys = new[]
    {
        "Escape", "Enter", "Tab", "Space", "Backspace", "Delete", "Insert",
        "Home", "End", "PageUp", "PageDown",
        "UpArrow", "DownArrow", "LeftArrow", "RightArrow",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Normalizes <paramref name="keybinding"/>, or explains why it cannot be used.</summary>
    /// <param name="keybinding">A binding such as <c>CtrlCmd+S</c>, <c>CtrlCmd+Shift+S</c>, or <c>F5</c>.</param>
    /// <param name="normalized">The Monaco-vocabulary form, such as <c>CtrlCmd+KeyS</c>.</param>
    /// <param name="problem">Why it was rejected, when it was.</param>
    public static bool TryNormalize(string keybinding, out string normalized, out string? problem)
    {
        normalized = string.Empty;
        problem = null;

        if (string.IsNullOrWhiteSpace(keybinding))
        {
            problem = "A keybinding cannot be empty.";
            return false;
        }

        var parts = keybinding.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            problem = $"'{keybinding}' contains no keys.";
            return false;
        }

        var tokens = new List<string>(parts.Length);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var modifier = Modifiers.FirstOrDefault(known => string.Equals(known, parts[i], StringComparison.OrdinalIgnoreCase));
            if (modifier is null)
            {
                problem = $"'{parts[i]}' is not a modifier. Use CtrlCmd, Shift, Alt, or WinCtrl.";
                return false;
            }

            tokens.Add(modifier);
        }

        if (!TryNormalizeKey(parts[^1], out var key))
        {
            problem = $"'{parts[^1]}' is not a key NovaSharp can bind.";
            return false;
        }

        tokens.Add(key);
        normalized = string.Join('+', tokens);
        return true;
    }

    private static bool TryNormalizeKey(string part, out string key)
    {
        // A single letter or digit is written the way a person would write it and normalized to Monaco's name, so a
        // command declares "CtrlCmd+S" rather than the enum member.
        if (part.Length == 1 && char.IsAsciiLetter(part[0]))
        {
            key = $"Key{char.ToUpperInvariant(part[0])}";
            return true;
        }

        if (part.Length == 1 && char.IsAsciiDigit(part[0]))
        {
            key = $"Digit{part[0]}";
            return true;
        }

        if (part.Length is 2 or 3
            && (part[0] is 'F' or 'f')
            && int.TryParse(part.AsSpan(1), out var number)
            && number is >= 1 and <= 19)
        {
            key = $"F{number}";
            return true;
        }

        var named = NamedKeys.FirstOrDefault(known => string.Equals(known, part, StringComparison.OrdinalIgnoreCase));
        if (named is not null)
        {
            key = named;
            return true;
        }

        // Already in Monaco's vocabulary, which is how a caller reaches a key this parser has no friendly form for.
        if (part.StartsWith("Key", StringComparison.Ordinal) || part.StartsWith("Digit", StringComparison.Ordinal))
        {
            key = part;
            return true;
        }

        key = string.Empty;
        return false;
    }
}

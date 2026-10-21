using System;
using System.Globalization;

namespace RageCoopAdmin.Client;

internal enum TextAppendResult
{
    Added,
    Empty,
    TooLong,
    InvalidText
}

// Limits are UTF-16 units, like the existing saved-name and search contracts.
// Appends and deletion never leave half a surrogate pair or combining sequence.
internal sealed class UnicodeTextBuffer
{
    private readonly int _maximumLength;
    internal string Text { get; private set; } = string.Empty;
    internal int MaximumLength => _maximumLength;

    internal UnicodeTextBuffer(int maximumLength)
    {
        if (maximumLength < 1 || maximumLength > 256) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        _maximumLength = maximumLength;
    }

    internal TextAppendResult Append(string value)
    {
        if (string.IsNullOrEmpty(value)) return TextAppendResult.Empty;
        if (!IsDisplayable(value)) return TextAppendResult.InvalidText;
        if (Text.Length + value.Length > _maximumLength) return TextAppendResult.TooLong;
        Text += value;
        return TextAppendResult.Added;
    }

    internal void DeleteLast()
    {
        if (Text.Length == 0) return;
        int[] elements = StringInfo.ParseCombiningCharacters(Text);
        Text = Text.Substring(0, elements[elements.Length - 1]);
    }

    private static bool IsDisplayable(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '~' || char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format) return false;
            if (char.IsHighSurrogate(character))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(character)) return false;
        }
        return true;
    }
}

using System;
using System.Drawing;
using System.Globalization;
using LemonUI.Elements;
using LemonUI.Menus;

namespace RageCoopAdmin.Client;

internal static class VehicleMenuText
{
    internal const string Create = "Создать";
    internal const string AddFavorite = "В избранное";
    internal const string RemoveFavorite = "Убрать из списка";
    internal const string Unavailable = "Недоступно";
    internal const string Delete = "Удалить";

    internal static readonly string[] Actions = { Create, AddFavorite, RemoveFavorite, Unavailable, Delete };

    // Measure with the same SDK text object, font and scale that will be drawn.
    // Text elements, rather than UTF-16 positions, keep a saved name's accents intact.
    internal static string Fit(ScaledText text, string fullText, float availableWidth)
    {
        text.Text = fullText;
        if (text.Width <= availableWidth) { return fullText; }
        text.Text = "…";
        if (text.Width > availableWidth) { text.Text = string.Empty; return text.Text; }
        int[] starts = StringInfo.ParseCombiningCharacters(fullText);
        int low = 0;
        int high = starts.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            text.Text = fullText.Substring(0, middle == starts.Length ? fullText.Length : starts[middle]).TrimEnd() + "…";
            if (text.Width <= availableWidth) { low = middle; }
            else { high = middle - 1; }
        }

        text.Text = fullText.Substring(0, low == starts.Length ? fullText.Length : starts[low]).TrimEnd() + "…";
        return text.Text;
    }

    internal static float ActionWidth(ScaledText text, System.Collections.Generic.IEnumerable<string> actions)
    {
        string selected = text.Text;
        float maximum = 0f;
        try
        {
            foreach (string action in actions)
            {
                text.Text = action;
                maximum = Math.Max(maximum, text.Width);
            }
            return maximum;
        }
        finally { text.Text = selected; }
    }
}

internal sealed class VehicleMenuListItem : NativeListItem<string>
{
    private const float TitleInset = 6f;
    // UpdateIndex is one pixel farther left than Recalculate in the installed SDK.
    private const float ActionInset = 2f;
    private const float ArrowWidth = 30f;
    private const float TextGap = 12f;
    private readonly string _fullTitle;

    internal VehicleMenuListItem(string title, string description, params string[] actions)
        : base(title, description, actions) { _fullTitle = title; }

    public override void Recalculate(PointF position, SizeF size, bool selected)
    {
        base.Recalculate(position, size, selected);
        // LemonUI uses 30 px for each selected arrow and draws the action
        // immediately before its right arrow. Reserve every supported action,
        // including the longer removal label, so toggling favorites cannot shift
        // a title underneath the action without a menu relayout.
        float actionWidth = Math.Max(VehicleMenuText.ActionWidth(text, VehicleMenuText.Actions),
            VehicleMenuText.ActionWidth(text, Items));
        float leftInset = TitleInset + (LeftBadge == null ? 0f : 34f);
        float available = size.Width - leftInset - ActionInset - TextGap - actionWidth -
            Math.Max(ArrowWidth, LeftArrow.Size.Width) - Math.Max(ArrowWidth, RightArrow.Size.Width);
        VehicleMenuText.Fit(title, _fullTitle, Math.Max(0f, available));
    }
}

internal sealed class VehicleMenuItem : NativeItem
{
    private string _fullTitle;

    internal VehicleMenuItem(string title, string description) : base(title, description) { _fullTitle = title; }

    internal string FullTitle
    {
        get => _fullTitle;
        set { _fullTitle = value ?? throw new ArgumentNullException(nameof(value)); Title = value; }
    }

    public override void Recalculate(PointF position, SizeF size, bool selected)
    {
        base.Recalculate(position, size, selected);
        float available = size.Width - 12f - (LeftBadge == null ? 0f : 34f) - (RightBadge == null ? 0f : 34f);
        VehicleMenuText.Fit(title, _fullTitle, Math.Max(0f, available));
    }
}

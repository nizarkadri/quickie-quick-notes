using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace StickyNotes.App.Windows;

/// <summary>
/// F4 — read-only hover preview for note chips/rows.
/// <para>
/// Builds a non-activating WPF <see cref="ToolTip"/>; it never touches focus and never counts as
/// "opening" a note. The caller resolves the body with <c>GetNote(id, markOpened: false)</c>
/// and fills the tooltip on <c>ToolTipOpening</c>.
/// </para>
/// </summary>
public static class NotePreview
{
    /// <summary>Max characters of body shown before truncation (roadmap F4: "first ~200 chars").</summary>
    public const int MaxChars = 200;

    /// <summary>Max tooltip width in DIPs before text wraps.</summary>
    public const double MaxWidth = 340;

    /// <summary>Hover dwell before the bubble appears. WPF's default is 1000 ms, which feels sluggish.</summary>
    public const int InitialShowDelayMs = 350;

    /// <summary>Gap between the chip/row and the bubble, in DIPs.</summary>
    public const double VerticalOffset = 6;

    private const string EmptyPlaceholder = "(empty note)";
    private const string Ellipsis = "\u2026";

    /// <summary>
    /// Collapse all whitespace runs to single spaces and truncate to <see cref="MaxChars"/>,
    /// preferring the last space before the limit so words are not split.
    /// </summary>
    public static string Format(string? body)
    {
        string normalized = CollapseWhitespace(body);
        if (normalized.Length == 0)
        {
            return EmptyPlaceholder;
        }

        if (normalized.Length <= MaxChars)
        {
            return normalized;
        }

        int cut = normalized.LastIndexOf(' ', MaxChars);
        if (cut < MaxChars / 2)
        {
            cut = MaxChars; // no sensible break point — hard cut instead of a sliver
        }

        return string.Concat(normalized.AsSpan(0, cut).TrimEnd(), Ellipsis);
    }

    /// <summary>
    /// The bubble content. Call on the UI thread (reads the App's TextPrimary brush).
    /// </summary>
    public static TextBlock CreateContent() => new()
    {
        Text = string.Empty,
        Foreground = (Brush)Application.Current.Resources["TextPrimary"],
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = MaxWidth,
    };

    /// <summary>
    /// Contract probe for the bubble properties. NOTE: WPF forbids attaching a ToolTip whose
    /// StaysOpen is false to an element (it throws NotSupportedException when shown), so the
    /// windows show <see cref="CreateContent"/> through ToolTipService instead, whose default
    /// lifetime dismisses when the cursor moves away.
    /// </summary>
    public static (ToolTip Tip, TextBlock Text) CreateToolTip()
    {
        TextBlock text = CreateContent();
        var tip = new ToolTip
        {
            Content = text,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
        };

        return (tip, text);
    }

    private static string CollapseWhitespace(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(body.Length);
        bool pendingSpace = false;
        foreach (char c in body)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0; // never lead with a space
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
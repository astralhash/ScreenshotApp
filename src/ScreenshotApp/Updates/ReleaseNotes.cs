using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ScreenshotApp.Updates;

/// <summary>
/// Turns the small subset of Markdown used in CHANGELOG.md / GitHub release notes into RichTextBlock paragraphs:
/// headings, "- " bullets, **bold**, *italic*, `code`, [links](https://…) and bare https:// URLs. Anything else is plain text.
/// </summary>
internal static partial class ReleaseNotes
{
    [GeneratedRegex(@"\*\*(?<b>.+?)\*\*|`(?<c>[^`]+)`|\[(?<lt>[^\]]+)\]\((?<lu>https?://[^)\s]+)\)|(?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*])|(?<u>https?://[^\s)]+)")]
    private static partial Regex InlinePattern();

    /// <summary>Adds the notes of each release (newest first) under a version heading.</summary>
    public static void Fill(Microsoft.UI.Xaml.Controls.RichTextBlock target, IEnumerable<ReleaseInfo> releases)
    {
        target.Blocks.Clear();
        bool first = true;
        foreach (var release in releases)
        {
            var heading = new Paragraph { Margin = new Thickness(0, first ? 0 : 18, 0, 6) };
            heading.Inlines.Add(new Run { Text = UpdateService.Format(release.Version), FontWeight = FontWeights.SemiBold, FontSize = 18 });
            if (release.Published != default)
                heading.Inlines.Add(new Run
                {
                    Text = "   " + release.Published.ToLocalTime().ToString("d MMMM yyyy"),
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    FontSize = 13,
                });
            target.Blocks.Add(heading);
            first = false;

            var lines = release.Notes.Split('\n');
            if (lines.All(string.IsNullOrWhiteSpace))
            {
                target.Blocks.Add(Plain("No release notes."));
                continue;
            }
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.TrimStart().StartsWith('#'))
                {
                    string text = line.TrimStart().TrimStart('#').Trim();
                    // The release's own "### 0.2.6" heading repeats the version shown above.
                    if (UpdateService.ParseVersion(text) == release.Version) continue;
                    var p = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                    p.Inlines.Add(new Run { Text = text, FontWeight = FontWeights.SemiBold });
                    target.Blocks.Add(p);
                }
                else if (line.TrimStart() is var t && (t.StartsWith("- ") || t.StartsWith("* ")))
                {
                    // Hanging indent: wrapped lines line up with the text, not the bullet.
                    var p = new Paragraph { Margin = new Thickness(16, 0, 0, 6), TextIndent = -12 };
                    p.Inlines.Add(new Run { Text = "•  " });
                    AddInlines(p, t[2..].Trim());
                    target.Blocks.Add(p);
                }
                else
                {
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                    AddInlines(p, line.Trim());
                    target.Blocks.Add(p);
                }
            }
        }
    }

    private static Paragraph Plain(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static void AddInlines(Paragraph p, string text)
    {
        int pos = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > pos) p.Inlines.Add(new Run { Text = text[pos..m.Index] });
            if (m.Groups["b"].Success)
                p.Inlines.Add(new Run { Text = m.Groups["b"].Value, FontWeight = FontWeights.SemiBold });
            else if (m.Groups["i"].Success)
                p.Inlines.Add(new Run { Text = m.Groups["i"].Value, FontStyle = Windows.UI.Text.FontStyle.Italic });
            else if (m.Groups["c"].Success)
                p.Inlines.Add(new Run { Text = m.Groups["c"].Value, FontFamily = new FontFamily("Cascadia Mono, Consolas") });
            else
            {
                bool bare = m.Groups["u"].Success;
                var link = new Hyperlink { NavigateUri = new Uri(bare ? m.Groups["u"].Value : m.Groups["lu"].Value) };
                link.Inlines.Add(new Run { Text = bare ? m.Groups["u"].Value : m.Groups["lt"].Value });
                p.Inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) p.Inlines.Add(new Run { Text = text[pos..] });
    }
}

using System.Text;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;

namespace AutoSubtitleGenerator;

/// <summary>A subtitle as Subtitle Edit's main window holds it, with the encoding "Save as" writes it in.</summary>
internal sealed record SubtitleDocument(Subtitle Subtitle, Encoding Encoding);

internal static class SubtitleFile
{
    /// <summary>A new subtitle, as after speech to text: in the default encoding.</summary>
    public static SubtitleDocument New(Subtitle subtitle) =>
        new(ThroughGrid(subtitle), EncodingHelper.ResolveEncoding(Se.Settings.General.DefaultEncoding, null));

    /// <summary>Opening a file: its own encoding, a UTF-8 file keeping its byte order mark or not.</summary>
    public static SubtitleDocument Load(string fileName)
    {
        var encoding = LanguageAutoDetect.GetEncodingFromFile(fileName);
        var subtitle = Subtitle.Parse(fileName, encoding);
        if (subtitle?.OriginalFormat is not SubRip || subtitle.Paragraphs.Count == 0)
        {
            throw new InvalidOperationException("Not a SubRip subtitle: " + fileName);
        }

        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            encoding = new UTF8Encoding(FileUtil.HasUtf8Bom(fileName));
        }

        return new SubtitleDocument(ThroughGrid(subtitle), encoding);
    }

    /// <summary>
    /// Writes to a temporary file first and renames it when complete, so an interrupted run
    /// never leaves a half-written file that a later run would skip as done.
    /// </summary>
    public static void Save(SubtitleDocument document, string fileName)
    {
        var text = document.Subtitle.ToText(new SubRip());
        if (Se.Settings.General.ForceCrLfOnSave)
        {
            text = EncodingHelper.ForceCrLf(text);
        }

        var tempFileName = fileName + ".tmp";
        File.WriteAllText(tempFileName, text, document.Encoding);
        File.Move(tempFileName, fileName, overwrite: true);
    }

    /// <summary>
    /// The main window's grid round trip (SubtitleLineViewModel in, ToParagraph out): times through
    /// TimeSpan, text trimmed at the end, numbered from 1.
    /// </summary>
    private static Subtitle ThroughGrid(Subtitle subtitle)
    {
        var result = new Subtitle(subtitle);
        result.Paragraphs.Clear();
        for (var i = 0; i < subtitle.Paragraphs.Count; i++)
        {
            var p = subtitle.Paragraphs[i];
            result.Paragraphs.Add(new Paragraph(
                new TimeCode(TimeSpan.FromMilliseconds(p.StartTime.TotalMilliseconds)),
                new TimeCode(TimeSpan.FromMilliseconds(p.EndTime.TotalMilliseconds)),
                (p.Text ?? string.Empty).TrimEnd())
            {
                Number = i + 1,
            });
        }

        return result;
    }
}

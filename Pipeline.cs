namespace AutoSubtitleGenerator;

/// <summary>
/// The three Subtitle Edit steps for one video, each skipped when its output exists. As in the
/// GUI, a step works on what the previous step left in memory, or on the saved file when that
/// step was skipped. A video that came with subtitles (<c>&lt;video&gt;.&lt;language&gt;.vtt</c>, as
/// yt-dlp writes them) has those translated instead.
/// </summary>
internal static class Pipeline
{
    private const string SourceLanguage = "de";
    private const string TargetLanguage = "nl";

    /// <summary>The subtitles a video may come with, in order of preference.</summary>
    public static readonly string[] SubtitleLanguages = [TargetLanguage, "en", SourceLanguage];

    public static async Task RunAsync(string videoFileName, CancellationToken cancellationToken)
    {
        var baseName = Path.Combine(Path.GetDirectoryName(videoFileName)!, Path.GetFileNameWithoutExtension(videoFileName));
        var transcribedFileName = $"{baseName}.{SourceLanguage}.srt";
        var alignedFileName = $"{baseName} (aligned).{SourceLanguage}.srt";
        var translatedFileName = $"{baseName}.{TargetLanguage}.srt";

        var subtitleLanguage = SubtitleLanguages.FirstOrDefault(language => File.Exists($"{baseName}.{language}.vtt"));
        if (subtitleLanguage != null)
        {
            await TranslateSubtitleAsync($"{baseName}.{subtitleLanguage}.vtt", subtitleLanguage, translatedFileName, cancellationToken);
            return;
        }

        SubtitleDocument? document = null;

        if (File.Exists(transcribedFileName))
        {
            Log.Step($"Speech to text: skipped, {Path.GetFileName(transcribedFileName)} exists");
        }
        else
        {
            Log.Step("Speech to text");
            document = SubtitleFile.New(await Transcribe.RunAsync(videoFileName, SourceLanguage, cancellationToken));
            SubtitleFile.Save(document, transcribedFileName);
            Log.Info("Saved " + Path.GetFileName(transcribedFileName));
        }

        if (File.Exists(alignedFileName))
        {
            Log.Step($"Improve time codes: skipped, {Path.GetFileName(alignedFileName)} exists");
            document = null;
        }
        else
        {
            Log.Step("Improve time codes");
            document ??= SubtitleFile.Load(transcribedFileName);
            document = document with { Subtitle = await Align.RunAsync(document.Subtitle, videoFileName, cancellationToken) };
            SubtitleFile.Save(document, alignedFileName);
            Log.Info("Saved " + Path.GetFileName(alignedFileName));
        }

        if (File.Exists(translatedFileName))
        {
            Log.Step($"Auto-translate: skipped, {Path.GetFileName(translatedFileName)} exists");
        }
        else
        {
            Log.Step("Auto-translate");
            document ??= SubtitleFile.Load(alignedFileName);
            document = document with { Subtitle = await Translate.RunAsync(document.Subtitle, SourceLanguage, TargetLanguage, cancellationToken) };
            SubtitleFile.Save(document, translatedFileName);
            Log.Info("Saved " + Path.GetFileName(translatedFileName));
        }
    }

    /// <summary>File > Open of the subtitle that came with the video, Auto-translate, then Save as SubRip.</summary>
    private static async Task TranslateSubtitleAsync(string subtitleFileName, string languageCode, string translatedFileName, CancellationToken cancellationToken)
    {
        if (languageCode == TargetLanguage)
        {
            Log.Step($"Subtitles: {Path.GetFileName(subtitleFileName)} is already in the target language, nothing to do");
            return;
        }

        if (File.Exists(translatedFileName))
        {
            Log.Step($"Auto-translate {Path.GetFileName(subtitleFileName)}: skipped, {Path.GetFileName(translatedFileName)} exists");
            return;
        }

        Log.Step($"Auto-translate {Path.GetFileName(subtitleFileName)} (speech to text and improve time codes not needed)");
        var document = SubtitleFile.Load(subtitleFileName);
        document = document with { Subtitle = await Translate.RunAsync(document.Subtitle, languageCode, TargetLanguage, cancellationToken) };
        SubtitleFile.Save(document, translatedFileName);
        Log.Info("Saved " + Path.GetFileName(translatedFileName));
    }
}

internal static class VideoFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg",
    };

    public static bool IsVideo(string fileName) => Extensions.Contains(Path.GetExtension(fileName));

    public static IEnumerable<string> Find(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .Order(StringComparer.OrdinalIgnoreCase);
}

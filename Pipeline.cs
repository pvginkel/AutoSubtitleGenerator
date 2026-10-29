namespace AutoSubtitleGenerator;

/// <summary>
/// The three Subtitle Edit steps for one video, each skipped when its output exists. As in the
/// GUI, a step works on what the previous step left in memory, or on the saved file when that
/// step was skipped.
/// </summary>
internal static class Pipeline
{
    private const string SourceLanguage = "de";
    private const string TargetLanguage = "nl";

    public static async Task RunAsync(string videoFileName, CancellationToken cancellationToken)
    {
        var baseName = Path.Combine(Path.GetDirectoryName(videoFileName)!, Path.GetFileNameWithoutExtension(videoFileName));
        var transcribedFileName = $"{baseName}.{SourceLanguage}.srt";
        var alignedFileName = $"{baseName} (aligned).{SourceLanguage}.srt";
        var translatedFileName = $"{baseName}.{TargetLanguage}.srt";

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
}

internal static class VideoFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg",
    };

    public static IEnumerable<string> Find(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f)))
            .Order(StringComparer.OrdinalIgnoreCase);
}

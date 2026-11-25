using System.Text;
using AutoSubtitleGenerator;
using Nikse.SubtitleEdit.Logic.Config;

// Subtitle Edit picks its data folder from where its executable is: outside Program Files it
// runs "portable" and keeps everything next to the exe. Pretend to be the installed copy, so
// Se's static constructor settles on %APPDATA%\Subtitle Edit with its settings, engines and models.
var installFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Subtitle Edit") + Path.DirectorySeparatorChar;
AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", installFolder);
var expectedDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subtitle Edit");
if (!string.Equals(Se.DataFolder.TrimEnd(Path.DirectorySeparatorChar), expectedDataFolder, StringComparison.OrdinalIgnoreCase))
{
    Log.Error($"Subtitle Edit data folder resolved to {Se.DataFolder}, expected {expectedDataFolder}.");
    return 1;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AutoSubtitleGenerator <video file or folder>...");
    Console.Error.WriteLine();
    Console.Error.WriteLine("For every video: speech to text (<video>.de.srt), improve time codes");
    Console.Error.WriteLine("(<video> (aligned).de.srt) and translation with Claude (<video>.nl.srt),");
    Console.Error.WriteLine("using the Subtitle Edit settings. A video with <video>.en.vtt, .en.srt or");
    Console.Error.WriteLine(".de.vtt next to it has that translated instead. A step is skipped when its");
    Console.Error.WriteLine("output file exists. Translation needs ANTHROPIC_API_KEY.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Folders are searched recursively, also for download.txt: a URL per line,");
    Console.Error.WriteLine("optionally followed by the file name, downloaded into its folder first");
    Console.Error.WriteLine("(.m3u8 with yt-dlp). Done lines get \"# \" in front and are skipped.");
    return 1;
}

var paths = new List<string>();
foreach (var arg in args)
{
    var path = Path.GetFullPath(arg);
    if (!Directory.Exists(path) && !File.Exists(path))
    {
        Log.Error("Not found: " + arg);
        return 1;
    }

    paths.Add(path);
}

// As Subtitle Edit's own startup and main window: code pages, the settings as the GUI left them
// (never saved back here), and the rule profile handed down to libse.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Se.LoadSettings();
Se.ApplyRuleSettingsToLibSe();

// A crashing engine exits rather than waiting on an error dialog; child processes inherit this.
NativeMethods.SetErrorMode(NativeMethods.SemFailCriticalErrors | NativeMethods.SemNoGpFaultErrorBox);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

// Downloads first, so what they bring is found as videos below.
var failedDownloads = new List<string>();
var listFileNames = paths.Where(Directory.Exists).SelectMany(Downloads.FindLists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
foreach (var listFileName in listFileNames)
{
    Header(listFileName);
    try
    {
        failedDownloads.AddRange(await Downloads.RunAsync(listFileName, cancellation.Token));
    }
    catch (OperationCanceledException)
    {
        Log.Error("Cancelled.");
        return 1;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Log.Error(exception.Message);
        failedDownloads.Add(listFileName);
    }
}

var videoFileNames = paths
    .SelectMany(path => Directory.Exists(path) ? VideoFiles.Find(path) : [path])
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

if (videoFileNames.Count == 0 && failedDownloads.Count == 0)
{
    Log.Error("No video files found.");
    return 1;
}

var failed = new List<string>();
foreach (var videoFileName in videoFileNames)
{
    Header(videoFileName);

    try
    {
        await Pipeline.RunAsync(videoFileName, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        Log.Error("Cancelled.");
        return 1;
    }
    catch (InvalidOperationException exception)
    {
        // Set-up problems and engine failures, with their own explanation.
        Log.Error(exception.Message);
        failed.Add(videoFileName);
    }
    catch (Exception exception)
    {
        Log.Error(exception.ToString());
        failed.Add(videoFileName);
    }
}

Console.WriteLine();
if (failedDownloads.Count > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Downloads failed ({failedDownloads.Count}):");
    failedDownloads.ForEach(f => Console.WriteLine("  " + f));
    Console.ResetColor();
}

if (failed.Count > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Failed ({failed.Count} of {videoFileNames.Count}):");
    failed.ForEach(f => Console.WriteLine("  " + f));
    Console.ResetColor();
}

if (failedDownloads.Count > 0 || failed.Count > 0)
{
    return 1;
}

Console.WriteLine($"Done ({videoFileNames.Count} video(s)).");
return 0;

static void Header(string text)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== " + text);
    Console.ResetColor();
}

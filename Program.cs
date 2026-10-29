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
    Console.Error.WriteLine("(<video> (aligned).de.srt) and DeepL translation (<video>.nl.srt), using");
    Console.Error.WriteLine("the Subtitle Edit settings. Folders are searched recursively. A step is");
    Console.Error.WriteLine("skipped when its output file exists. DeepL needs DEEPL_API_KEY.");
    return 1;
}

var videoFileNames = new List<string>();
foreach (var arg in args)
{
    var path = Path.GetFullPath(arg);
    if (Directory.Exists(path))
    {
        videoFileNames.AddRange(VideoFiles.Find(path));
    }
    else if (File.Exists(path))
    {
        videoFileNames.Add(path);
    }
    else
    {
        Log.Error("Not found: " + arg);
        return 1;
    }
}

if (videoFileNames.Count == 0)
{
    Log.Error("No video files found.");
    return 1;
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

var failed = new List<string>();
foreach (var videoFileName in videoFileNames)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("=== " + videoFileName);
    Console.ResetColor();

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
if (failed.Count > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Failed ({failed.Count} of {videoFileNames.Count}):");
    failed.ForEach(f => Console.WriteLine("  " + f));
    Console.ResetColor();
    return 1;
}

Console.WriteLine($"Done ({videoFileNames.Count} video(s)).");
return 0;

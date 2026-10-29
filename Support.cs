using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Nikse.SubtitleEdit.Logic.Config;

namespace AutoSubtitleGenerator;

internal static class Log
{
    private static bool _inProgress;

    public static void Step(string text)
    {
        EndProgress();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static void Info(string text)
    {
        EndProgress();
        Console.WriteLine("  " + text);
    }

    public static void Error(string text)
    {
        EndProgress();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("  " + text);
        Console.ResetColor();
    }

    /// <summary>A status line that the next one overwrites.</summary>
    public static void Progress(string text)
    {
        var width = Console.IsOutputRedirected ? 0 : Math.Max(0, Console.WindowWidth - 1);
        var line = ("  " + text).PadRight(width);
        if (width > 0 && line.Length > width)
        {
            line = line[..width];
        }

        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(line);
            return;
        }

        Console.Write("\r" + line);
        _inProgress = true;
    }

    public static void EndProgress()
    {
        if (_inProgress)
        {
            Console.WriteLine();
            _inProgress = false;
        }
    }
}

internal static class Ffmpeg
{
    /// <summary>The ffmpeg Subtitle Edit is set up with, as its GetFfmpegPath helpers pick it.</summary>
    public static string Path => File.Exists(Se.Settings.General.FfmpegPath) ? Se.Settings.General.FfmpegPath : "ffmpeg";

    public static async Task<(int ExitCode, string Output)> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // Or ffmpeg reads key presses from this console.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* already gone */ }
            throw;
        }

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}

internal static class NativeMethods
{
    public const uint SemFailCriticalErrors = 0x0001;
    public const uint SemNoGpFaultErrorBox = 0x0002;

    [DllImport("kernel32.dll")]
    public static extern uint SetErrorMode(uint mode);
}

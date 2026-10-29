using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Features.Video.SpeechToText;
using Nikse.SubtitleEdit.Features.Video.SpeechToText.Engines;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;
using Nikse.SubtitleEdit.UiLogic.AudioToText;
using Nikse.SubtitleEdit.UiLogic.Media;

namespace AutoSubtitleGenerator;

/// <summary>
/// Video > Speech to text with Purfview's Faster-Whisper-XXL in single mode: the orchestration of
/// SpeechToTextViewModel (GenerateAudioFile, TranscribeViaWhisper, GetWhisperProcess,
/// GetResultFromSrt, PostProcess), without the window.
/// </summary>
internal static class Transcribe
{
    public static async Task<Subtitle> RunAsync(string videoFileName, string languageCode, CancellationToken cancellationToken)
    {
        var settings = Se.Settings.Tools.AudioToText;
        if (settings.WhisperChoice != WhisperChoice.PurfviewFasterWhisperXxl)
        {
            throw new InvalidOperationException(
                $"Only Purfview's Faster-Whisper-XXL is supported, but Subtitle Edit is set to \"{settings.WhisperChoice}\".");
        }

        var engine = new WhisperEnginePurfviewFasterWhisperXxl();
        var executable = engine.GetExecutable();
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException($"Faster-Whisper-XXL is not installed ({executable}) - download it from Speech to text in Subtitle Edit.");
        }

        var model = engine.Models.FirstOrDefault(m => m.Name == settings.WhisperModel)
                    ?? throw new InvalidOperationException($"Unknown Whisper model \"{settings.WhisperModel}\".");
        if (!engine.IsModelInstalled(model))
        {
            throw new InvalidOperationException($"Whisper model \"{model.Name}\" is not downloaded - download it from Speech to text in Subtitle Edit.");
        }

        if (!File.Exists(Se.Settings.General.FfmpegPath))
        {
            throw new InvalidOperationException($"ffmpeg not found ({Se.Settings.General.FfmpegPath}) - set it up in Subtitle Edit.");
        }

        if (Se.Settings.General.FfmpegUseCenterChannelOnly)
        {
            throw new InvalidOperationException("\"Use only front center channel for 5.1 audio\" is not supported - turn it off in Subtitle Edit's options.");
        }

        var audioTracks = FfmpegMediaInfo.Parse(videoFileName).Tracks.Where(t => t.TrackType == FfmpegTrackType.Audio).ToList();
        if (audioTracks.Count == 0)
        {
            throw new InvalidOperationException("The video has no audio track.");
        }

        // The GUI takes the track the video player picked; that is the first one unless chosen otherwise.
        var audioStreamIndex = audioTracks[0].StreamIndex;
        Log.Info($"Engine: {engine.Name}, model {model.Name}, language {languageCode}, parameters \"{engine.CommandLineParameter}\"");

        var sttTempFolder = Path.Combine(Path.GetTempPath(), "se-stt-" + Guid.NewGuid());
        Directory.CreateDirectory(sttTempFolder);
        try
        {
            // GenerateAudioFile; the GUI makes the same file once more for MakeWavePeaks.
            var waveFileName = Path.Combine(sttTempFolder, Guid.NewGuid() + ".wav");
            await ExtractAudioAsync(videoFileName, audioStreamIndex, waveFileName, cancellationToken);

            // TranscribeViaWhisper: Purfview reads the video itself when there is just the one audio track.
            var inputFileName = audioTracks.Count == 1 ? videoFileName : waveFileName;
            var output = await RunWhisperAsync(engine, model, languageCode, inputFileName, sttTempFolder, cancellationToken);

            var resultFileName = Path.Combine(sttTempFolder, Path.GetFileNameWithoutExtension(inputFileName) + ".srt");
            if (!File.Exists(resultFileName))
            {
                throw new InvalidOperationException("Faster-Whisper-XXL wrote no subtitle file. Last output:" + Environment.NewLine + Tail(output));
            }

            var transcript = LoadResult(resultFileName);
            transcript = SpeechToTextTimingFixer.SortAndRemoveOverlaps(transcript);
            Log.Info($"Transcribed {transcript.Paragraphs.Count} lines");

            return PostProcess(transcript, languageCode, waveFileName, engine.CommandLineParameter);
        }
        finally
        {
            try
            {
                Directory.Delete(sttTempFolder, true);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }
    }

    private static async Task ExtractAudioAsync(string videoFileName, int audioStreamIndex, string waveFileName, CancellationToken cancellationToken)
    {
        // GetFfmpegProcess with GetFfmpegTranscodeFormatString("wav", centerChannelOnly: false).
        var map = audioStreamIndex >= 0 ? $"-map 0:{audioStreamIndex}?" : string.Empty;
        var arguments = $"-i \"{videoFileName}\" -vn -ar 16000 -af aresample=async=1:first_pts=0 -ac 1 -c:a pcm_s16le -f wav {map} \"{waveFileName}\"";

        Log.Progress("Extracting audio");
        var (exitCode, output) = await Ffmpeg.RunAsync(arguments, cancellationToken);
        Log.EndProgress();
        if (exitCode != 0 || !File.Exists(waveFileName))
        {
            throw new InvalidOperationException("Could not extract the audio with ffmpeg: " + output);
        }
    }

    private static async Task<List<string>> RunWhisperAsync(
        WhisperEnginePurfviewFasterWhisperXxl engine,
        WhisperModel model,
        string languageCode,
        string inputFileName,
        string outputFolder,
        CancellationToken cancellationToken)
    {
        // GetWhisperProcess, generic branch.
        var args = engine.CommandLineParameter;
        var outputDirArg = !args.Contains("--output_dir", StringComparison.Ordinal) && !Regex.IsMatch(args, @"(^|\s)-o(\s|$)")
            ? $"--output_dir \"{outputFolder}\" "
            : string.Empty;
        var arguments = $"--language {languageCode} --model \"{engine.GetModelForCmdLine(model.Name)}\" {outputDirArg}{args} \"{inputFileName}\"";

        var startInfo = new ProcessStartInfo(engine.GetExecutable(), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(engine.GetExecutable()),
        };

        var path = (startInfo.Environment["Path"] ?? string.Empty).TrimEnd(';');
        path += ";" + Path.GetDirectoryName(Se.Settings.General.FfmpegPath);
        path += ";" + Path.GetDirectoryName(engine.GetExecutable());
        startInfo.Environment["Path"] = path;
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var output = new List<string>();
        void OnOutput(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (output)
            {
                output.Add(line);
            }

            Log.Progress(line.Trim());
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        Se.WriteToolsLog($"{startInfo.FileName} {arguments}");
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

        Log.EndProgress();

        // The failures OutputHandler watches for; the GUI then ignores any result file.
        string[] errors =
        [
            "unrecognized argument",
            "CUDA out of memory",
            "CUDA failed with error out of memory",
            "CUBLAS_STATUS_NOT_SUPPORTED",
            "Torch not compiled with CUDA",
        ];
        lock (output)
        {
            var error = output.FirstOrDefault(line => errors.Any(e => line.Contains(e, StringComparison.OrdinalIgnoreCase)));
            if (error != null)
            {
                throw new InvalidOperationException("Faster-Whisper-XXL failed: " + error.Trim() + Environment.NewLine + Tail(output));
            }

            return output.ToList();
        }
    }

    /// <summary>GetResultFromSrt, then the decimal seconds of ResultText back to paragraphs.</summary>
    private static Subtitle LoadResult(string fileName)
    {
        var raw = new Subtitle();
        new SubRip().LoadSubtitle(raw, FileUtil.ReadAllLinesShared(fileName, Encoding.UTF8), fileName);
        raw.RemoveEmptyLines();

        var subtitle = new Subtitle();
        foreach (var p in raw.Paragraphs)
        {
            var start = (decimal)p.StartTime.TotalSeconds;
            var end = (decimal)p.EndTime.TotalSeconds;
            subtitle.Paragraphs.Add(new Paragraph(p.Text, (double)start * 1000.0, (double)end * 1000.0));
        }

        return subtitle;
    }

    /// <summary>SpeechToTextViewModel.PostProcess.</summary>
    private static Subtitle PostProcess(Subtitle transcript, string languageCode, string waveFileName, string engineArguments)
    {
        var settings = Se.Settings.Tools.AudioToText;
        var postProcessor = new SpeechToTextPostProcessor(languageCode)
        {
            ParagraphMaxChars = SpeechToTextPostProcessor.GetParagraphMaxChars(),
            RemoveNonSpeechLines = settings.WhisperPostProcessingRemoveNonSpeechLines,
            RemoveRepeatedLines = settings.WhisperPostProcessingRemoveRepeatedLines,
            EngineCommandLineArguments = engineArguments,
        };

        if (settings.WhisperAutoAdjustTimings)
        {
            // MakeWavePeaks: from the same 16 kHz extraction; the GUI also caches the peaks, which is left out here.
            Log.Progress("Adjusting timings");
            using var peakGenerator = new WavePeakGenerator2(waveFileName);
            var wavePeaks = peakGenerator.GeneratePeaks(0, string.Empty);
            transcript = SpeechToTextTimingFixer.ShortenLongDuration(transcript);
            transcript = SpeechToTextTimingFixer.ShortenViaWavePeaks(transcript, wavePeaks);
        }

        Log.Progress("Post-processing");
        transcript = postProcessor.Fix(
            SpeechToTextPostProcessor.Engine.Whisper,
            transcript,
            settings.PostProcessing,
            settings.WhisperPostProcessingAddPeriods,
            settings.WhisperPostProcessingMergeLines,
            settings.WhisperPostProcessingFixCasing,
            settings.WhisperPostProcessingFixShortDuration,
            settings.WhisperPostProcessingSplitLines,
            settings.WhisperPostProcessingChangeUnderlineToColor,
            settings.WhisperPostProcessingChangeUnderlineToColorColor.FromHexToColor());
        Log.EndProgress();
        Log.Info(postProcessor.QualityReport.ToLogString());

        return transcript;
    }

    private static string Tail(IReadOnlyList<string> lines) =>
        string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 20)));
}

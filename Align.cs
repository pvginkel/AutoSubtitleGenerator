using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Files.ImportPlainText;
using Nikse.SubtitleEdit.Features.Tools.ImproveTimeCodes;
using Nikse.SubtitleEdit.Features.Video.SpeechToText.Engines;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.UiLogic.Media;

namespace AutoSubtitleGenerator;

/// <summary>
/// Tools > Improve time codes (forced alignment), then OK: the orchestration of
/// ImproveTimeCodesViewModel.Align, ShowResults and RebuildAligned, without the window.
/// </summary>
internal static class Align
{
    public static async Task<Subtitle> RunAsync(Subtitle subtitle, string videoFileName, CancellationToken cancellationToken)
    {
        var settings = Se.Settings.Tools.ImproveTimeCodes;
        if (settings.IsolateSpeech || settings.CheckWithSpeechToText)
        {
            // Both need more of the dialog than is copied here; fail rather than quietly align differently.
            throw new InvalidOperationException(
                "\"Isolate speech first\" and \"Check with speech-to-text\" are not supported - turn them off in Improve time codes.");
        }

        // As MainViewModel.ShowImproveTimeCodes: lines in start time order, language from the text.
        var paragraphs = subtitle.Paragraphs.OrderBy(p => p.StartTime.TotalMilliseconds).ToList();
        var languageCode = LanguageAutoDetect.AutoDetectGoogleLanguage(subtitle);

        var engine = new CrispAsrParakeet();
        var executable = engine.GetExecutable();
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException($"Crisp ASR is not installed ({executable}) - download it from Improve time codes in Subtitle Edit.");
        }

        // As ImproveTimeCodesViewModel.Initialize: ranked for the language, the configured aligner if installed.
        var aligners = ImproveTimeCodesAligners.Rank(languageCode);
        foreach (var option in aligners)
        {
            option.IsInstalled = File.Exists(engine.GetModelForCmdLine(option.FileName));
        }

        var aligner = aligners.FirstOrDefault(a => a.Choice == settings.Aligner && a.IsInstalled)
                      ?? aligners.FirstOrDefault()
                      ?? throw new InvalidOperationException("No forced aligner available.");
        var modelPath = engine.GetModelForCmdLine(aligner.FileName);
        if (!File.Exists(modelPath))
        {
            throw new InvalidOperationException($"Aligner model \"{aligner.BaseDisplay}\" is not downloaded ({modelPath}) - pick it once in Improve time codes in Subtitle Edit.");
        }

        Log.Info($"Aligner: {aligner.BaseDisplay} (language {languageCode}), max shift {settings.MaxShiftSeconds:0.0}s");

        var workFolder = Path.Combine(Path.GetTempPath(), "se-improve-time-codes-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(workFolder);
            var audioFileName = Path.Combine(workFolder, "audio.wav");
            await ExtractAudioAsync(videoFileName, audioFileName, cancellationToken);

            // 16 kHz, mono, 16-bit, behind a 44 byte header.
            var totalSeconds = Math.Max(0, new FileInfo(audioFileName).Length - 44) / (16000.0 * 2);

            var general = Se.Settings.General;
            var minGapSeconds = general.MinimumBetweenLines.GetMilliseconds() / 1000.0;
            var options = new SubtitleRetimer.Options
            {
                MaxShiftSeconds = (double)(decimal)Math.Clamp(settings.MaxShiftSeconds, 0.1, 10.0),
                AdjustStart = settings.AdjustStart,
                AdjustEnd = settings.AdjustEnd,
                MinDurationSeconds = general.SubtitleMinimumDisplayMilliseconds / 1000.0,
                MaxDurationSeconds = general.SubtitleMaximumDisplayMilliseconds / 1000.0,
                ReadingCharsPerSecond = general.SubtitleOptimalCharactersPerSeconds,
                MaxCharsPerSecond = general.SubtitleMaximumCharactersPerSeconds,
                MinGapSeconds = minGapSeconds,
            };

            var lines = paragraphs
                .Select(p => new SubtitleRetimer.Line(p.Text ?? string.Empty, p.StartTime.TotalSeconds, p.EndTime.TotalSeconds))
                .ToList();

            var lastBatch = 0;
            var progress = new Progress<SubtitleRetimer.Progress>(p =>
            {
                if (p.BatchIndex != lastBatch)
                {
                    lastBatch = p.BatchIndex;
                    Log.Progress($"Aligning batch {p.BatchIndex} of {p.BatchCount}");
                }
            });

            using var audio = new FfmpegWindowAudioSource(Ffmpeg.Path, audioFileName, totalSeconds, workFolder);
            var runner = new CrispAsrAlignOnlyRunner(executable, modelPath, Se.WriteToolsLog);
            var results = await new SubtitleRetimer(runner, audio, options).RetimeAsync(lines, progress, cancellationToken, null);
            Log.EndProgress();

            // ShowResults/ImproveTimeCodesRow.SetResult: a move is applied unless it is unconfirmed;
            // RebuildAligned: every other line goes to its fallback place, then all is settled.
            var placements = new SubtitleRetimer.Placement[lines.Count];
            var retimed = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var result = results[i];
                var isChanged = SubtitleRetimer.IsMove(result.Status);
                var apply = isChanged && !SubtitleRetimer.IsUnconfirmed(result.Status);
                if (apply)
                {
                    placements[i] = new SubtitleRetimer.Placement(result.StartSeconds, result.EndSeconds, true);
                    retimed++;
                }
                else
                {
                    var (start, end) = result.Fallback ?? (lines[i].StartSeconds, lines[i].EndSeconds);
                    placements[i] = new SubtitleRetimer.Placement(start, end, false);
                }
            }

            SubtitleRetimer.Settle(lines, placements, minGapSeconds);
            if (general.UseFrameMode)
            {
                SubtitleRetimer.SnapToFrames(lines, placements, general.CurrentFrameRate, minGapSeconds);
            }

            var toCheck = results.Count(r => SubtitleRetimer.IsUnconfirmed(r.Status));
            var noSpeech = results.Count(r => r.Status == SubtitleRetimer.LineStatus.NoSpeech);
            Log.Info($"Retimed {retimed}, kept {lines.Count - retimed - toCheck - noSpeech}, no speech {noSpeech}, to check (left as is) {toCheck}");

            // MainViewModel.ShowImproveTimeCodes: only the times change, line by line.
            var aligned = new Subtitle(subtitle);
            aligned.Paragraphs.Clear();
            for (var i = 0; i < paragraphs.Count; i++)
            {
                aligned.Paragraphs.Add(new Paragraph(paragraphs[i])
                {
                    StartTime = new TimeCode(Math.Round(placements[i].StartSeconds * 1000.0)),
                    EndTime = new TimeCode(Math.Round(placements[i].EndSeconds * 1000.0)),
                });
            }

            aligned.Renumber();
            return aligned;
        }
        finally
        {
            TryDeleteFolder(workFolder);
        }
    }

    private static async Task ExtractAudioAsync(string videoFileName, string audioFileName, CancellationToken cancellationToken)
    {
        // ImproveTimeCodesViewModel.ExtractAudioAsync, with the audio track the video player
        // picked - the first one, unless chosen otherwise.
        var audioTrack = FfmpegMediaInfo.Parse(videoFileName).Tracks.FirstOrDefault(t => t.TrackType == FfmpegTrackType.Audio);
        var map = audioTrack != null ? $"-map 0:{audioTrack.StreamIndex}?" : string.Empty;
        var arguments =
            $"-hide_banner -nostats -loglevel error -y -i \"{videoFileName}\" -vn {map} -af aresample=async=1:first_pts=0 -ar 16000 -ac 1 -acodec pcm_s16le \"{audioFileName}\"";

        Log.Progress("Extracting audio");
        var (exitCode, output) = await Ffmpeg.RunAsync(arguments, cancellationToken);
        Log.EndProgress();
        if (exitCode != 0 || !File.Exists(audioFileName))
        {
            throw new InvalidOperationException("Could not extract the audio with ffmpeg: " + output);
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}

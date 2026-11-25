using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoSubtitleGenerator;

/// <summary>
/// download.txt: a URL per line, optionally followed by whitespace and the file name to save it
/// as, in the folder of download.txt. An .m3u8 URL is downloaded with yt-dlp, anything else
/// directly. A downloaded line gets "# " in front; lines starting with # are skipped.
/// </summary>
internal static class Downloads
{
    public const string ListFileName = "download.txt";
    private const string DoneMarker = "# ";

    /// <summary>
    /// Video in H.264 as before; audio in the original version ("multiple languages"), English or
    /// German, in that order, else the best there is.
    /// </summary>
    private const string FormatSelector =
        "bv*[vcodec^=avc1]+ba[language=mul]/bv*[vcodec^=avc1]+ba[language^=en]/bv*[vcodec^=avc1]+ba[language^=de]/bv*[vcodec^=avc1]+ba/b";

    /// <summary>What yt-dlp can merge into.</summary>
    private static readonly HashSet<string> MergeFormats = new(StringComparer.OrdinalIgnoreCase) { "mp4", "mkv", "webm", "mov", "avi", "flv" };

    private static readonly Dictionary<string, string[]> LanguageCodes = new()
    {
        ["nl"] = ["nl", "nld", "dut"],
        ["en"] = ["en", "eng"],
        ["de"] = ["de", "deu", "ger"],
    };

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = CreateHttpClient();

    public static IEnumerable<string> FindLists(string folder) =>
        Directory.EnumerateFiles(folder, ListFileName, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>Downloads every line not done yet; returns the lines that failed.</summary>
    public static async Task<List<string>> RunAsync(string listFileName, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(listFileName)!;
        var failed = new List<string>();
        var handled = new HashSet<string>(StringComparer.Ordinal);

        // The list is read again for every line, so lines added while downloading are picked up
        // and marking a line done never undoes such an edit.
        while (DownloadList.Read(listFileName).Pending().FirstOrDefault(l => !handled.Contains(l)) is { } line)
        {
            handled.Add(line);
            try
            {
                await DownloadAsync(line, folder, cancellationToken);
                MarkDone(listFileName, line);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Log.Error(exception is InvalidOperationException or HttpRequestException or IOException ? exception.Message : exception.ToString());
                failed.Add($"{listFileName}: {line}");
            }
        }

        return failed;
    }

    private static async Task DownloadAsync(string line, string folder, CancellationToken cancellationToken)
    {
        var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var url = parts[0];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Step("Download " + line);
            throw new InvalidOperationException("Not an http(s) URL: " + url);
        }

        var useYtDlp = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        var fileName = parts.Length > 1 ? parts[1].Trim('"').Trim() : string.Empty;
        if (fileName.Length == 0)
        {
            // From the URL: its last path segment, without the .m3u8.
            fileName = Uri.UnescapeDataString(uri.Segments[^1]).Trim('/').Trim();
            if (useYtDlp)
            {
                fileName = Path.GetFileNameWithoutExtension(fileName);
            }
        }

        if (fileName.Length == 0)
        {
            Log.Step("Download " + url);
            throw new InvalidOperationException("No file name in the URL; put one after it.");
        }

        if (!VideoFiles.IsVideo(fileName))
        {
            fileName += ".mp4";
        }

        Log.Step("Download " + fileName);
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Not a valid file name: \"{fileName}\".");
        }

        var target = Path.Combine(folder, fileName);
        if (File.Exists(target))
        {
            Log.Info("Skipped, it exists");
            return;
        }

        Log.Info("From " + url);
        if (useYtDlp)
        {
            await DownloadWithYtDlpAsync(url, folder, fileName, cancellationToken);
        }
        else
        {
            await DownloadDirectAsync(url, target, cancellationToken);
        }

        Log.Info("Saved " + fileName);
    }

    private static async Task DownloadDirectAsync(string url, string target, CancellationToken cancellationToken)
    {
        // Into a temporary file that is renamed when complete, like the subtitles, so an
        // interrupted download is never taken for a finished one.
        var partFileName = target + ".part";
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var total = response.Content.Headers.ContentLength;
            long done = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var output = new FileStream(partFileName, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                var sinceReport = Stopwatch.StartNew();
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await input.ReadAsync(buffer, stall.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    if (sinceReport.ElapsedMilliseconds >= 500)
                    {
                        sinceReport.Restart();
                        Log.Progress(total > 0
                            ? $"Downloading {Megabytes(done)} of {Megabytes(total.Value)} ({100.0 * done / total.Value:0}%)"
                            : $"Downloading {Megabytes(done)}");
                    }
                }
            }

            Log.EndProgress();
            if (total.HasValue && done != total.Value)
            {
                throw new InvalidOperationException($"Download incomplete: {done} of {total.Value} bytes.");
            }

            File.Move(partFileName, target);
            Log.Info($"Downloaded {Megabytes(done)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Download stalled: nothing received for {StallTimeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            Log.EndProgress();
            TryDelete(partFileName);
        }
    }

    private static async Task DownloadWithYtDlpAsync(string url, string folder, string fileName, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.');
        if (!MergeFormats.Contains(extension))
        {
            throw new InvalidOperationException($"yt-dlp cannot save as .{extension}; use .{string.Join(", .", MergeFormats)}.");
        }

        var subtitle = await ChooseSubtitleAsync(url, cancellationToken);

        // yt-dlp works in a temporary folder and moves the finished files (subtitles first, then
        // the video) into the target folder, so a half-done download is never found as a video.
        // The folder stays the same for a file, so a download that was interrupted is continued.
        var target = Path.Combine(folder, fileName);
        var tempFolder = Path.Combine(Path.GetTempPath(), "AutoSubtitleGenerator",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..16]);
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        List<string> arguments =
        [
            "--newline",
            "--format", FormatSelector,
            "--merge-output-format", extension,
            "--remux-video", extension,
            "--paths", "home:" + folder,
            "--paths", "temp:" + tempFolder,
            "--output", baseName.Replace("%", "%%") + ".%(ext)s",
        ];
        if (subtitle != null)
        {
            arguments.AddRange(["--write-subs", "--sub-langs", Regex.Escape(subtitle.Value.Language)]);
        }

        arguments.AddRange(["--", url]);

        var output = await RunYtDlpAsync(arguments, cancellationToken);
        if (subtitle != null)
        {
            // Named by its two-letter code, as the pipeline looks for it.
            var written = Path.Combine(folder, $"{baseName}.{subtitle.Value.Language}.vtt");
            var expected = Path.Combine(folder, $"{baseName}.{subtitle.Value.Code}.vtt");
            if (!string.Equals(written, expected, StringComparison.Ordinal) && File.Exists(written))
            {
                File.Move(written, expected, overwrite: true);
            }

            if (!File.Exists(expected))
            {
                Log.Error($"yt-dlp wrote no {Path.GetFileName(expected)}.");
            }
        }

        if (!File.Exists(target))
        {
            throw new InvalidOperationException($"yt-dlp finished, but there is no {fileName}. Last output:" + Environment.NewLine + Tail(output));
        }

        TryDeleteFolder(tempFolder);
    }

    /// <summary>
    /// The subtitles to download: by language in the pipeline's order of preference, from the
    /// tracks the playlist lists. Forced subtitles leave out what is spoken in their own language.
    /// English needs no subtitles, so forced English tracks are taken; forced tracks in other
    /// languages are skipped. yt-dlp keeps the tracks by language and takes the last one of it.
    /// </summary>
    private static async Task<(string Language, string Code)?> ChooseSubtitleAsync(string url, CancellationToken cancellationToken)
    {
        string playlist;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(StallTimeout);
            try
            {
                playlist = await Http.GetStringAsync(url, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"No playlist: nothing received for {StallTimeout.TotalSeconds:0} seconds.");
            }
        }

        // yt-dlp's _parse_m3u8_formats_and_subtitles: TYPE, GROUP-ID, NAME and URI required.
        var tracks = playlist.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            .Select(l => ParseAttributes(l["#EXT-X-MEDIA:".Length..]))
            .Where(a => a.GetValueOrDefault("TYPE") == "SUBTITLES" && a.ContainsKey("GROUP-ID") && a.ContainsKey("NAME") && a.ContainsKey("URI"))
            .GroupBy(a => a.GetValueOrDefault("LANGUAGE") ?? "und")
            .Select(g => (Language: g.Key, Track: g.Last()))
            .ToList();

        foreach (var code in Pipeline.SubtitleLanguages)
        {
            var codes = LanguageCodes.GetValueOrDefault(code) ?? [code];
            foreach (var (language, track) in tracks.Where(t => codes.Contains(t.Language.Split('-')[0], StringComparer.OrdinalIgnoreCase)))
            {
                var name = $"{track["NAME"]} ({language})";
                if (code != "en" && string.Equals(track.GetValueOrDefault("FORCED"), "YES", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"Subtitles {name}: skipped, forced subtitles only cover part of the video");
                    continue;
                }

                Log.Info($"Subtitles: {name}");
                return (language, code);
            }
        }

        Log.Info(tracks.Count == 0
            ? "Subtitles: none"
            : $"Subtitles: none to use (there are {string.Join(", ", tracks.Select(t => $"{t.Track["NAME"]} ({t.Language})"))})");
        return null;
    }

    /// <summary>An attribute list: NAME=VALUE pairs, the values possibly quoted, separated by commas.</summary>
    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, "([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)"))
        {
            attributes[match.Groups[1].Value] = match.Groups[2].Value.Trim('"');
        }

        return attributes;
    }

    private static async Task<List<string>> RunYtDlpAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("yt-dlp")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var output = new List<string>();
        void OnOutput(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            line = line.Trim();
            lock (output)
            {
                output.Add(line);
            }

            // What was picked and what went wrong stays on screen; progress overwrites itself.
            if (line.StartsWith("ERROR:", StringComparison.Ordinal) ||
                line.StartsWith("WARNING:", StringComparison.Ordinal) ||
                (line.StartsWith("[info]", StringComparison.Ordinal) && line.Contains(": Downloading", StringComparison.Ordinal)))
            {
                Log.Info(line);
            }
            else
            {
                Log.Progress(line);
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Could not run yt-dlp ({exception.Message}) - install it and make sure it is on the PATH.");
        }

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
        lock (output)
        {
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp failed (exit code {process.ExitCode}). Last output:" + Environment.NewLine + Tail(output));
            }

            return output.ToList();
        }
    }

    private static void MarkDone(string listFileName, string line)
    {
        var list = DownloadList.Read(listFileName);
        var index = list.Lines.FindIndex(l => l.Trim() == line);
        if (index < 0)
        {
            Log.Info($"Could not mark the line done in {ListFileName}: it was changed meanwhile");
            return;
        }

        list.Lines[index] = DoneMarker + list.Lines[index].TrimStart();
        list.Write();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
        {
            // The stall timeout covers it, also for the body.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoSubtitleGenerator/1.0");
        return client;
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0.0} MB";

    private static string Tail(IReadOnlyList<string> lines) =>
        string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 20)));

    private static void TryDelete(string fileName)
    {
        try
        {
            File.Delete(fileName);
        }
        catch
        {
            // Best effort.
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

    /// <summary>download.txt as text lines, written back in its own encoding and line breaks.</summary>
    private sealed class DownloadList
    {
        private DownloadList(string fileName, Encoding encoding, string newLine, List<string> lines)
        {
            FileName = fileName;
            Encoding = encoding;
            NewLine = newLine;
            Lines = lines;
        }

        public string FileName { get; }
        public Encoding Encoding { get; }
        public string NewLine { get; }
        public List<string> Lines { get; }

        public static DownloadList Read(string fileName)
        {
            using var reader = new StreamReader(fileName, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            return new DownloadList(fileName, reader.CurrentEncoding, newLine, lines);
        }

        /// <summary>The lines still to download, trimmed.</summary>
        public IEnumerable<string> Pending() =>
            Lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

        public void Write()
        {
            var tempFileName = FileName + ".tmp";
            File.WriteAllText(tempFileName, string.Join(NewLine, Lines), Encoding);
            File.Move(tempFileName, FileName, overwrite: true);
        }
    }
}

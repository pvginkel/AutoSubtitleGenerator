# AutoSubtitleGenerator

Makes German and Dutch subtitles for videos by doing what is otherwise done by hand in
Subtitle Edit, with the same engines, models and settings:

| Step | Subtitle Edit | Output |
|---|---|---|
| 1 | Video > Speech to text (Purfview Faster-Whisper-XXL) | `<video>.de.srt` |
| 2 | Tools > Improve time codes (Crisp ASR forced alignment) | `<video> (aligned).de.srt` |
| 3 | Translate > Auto-translate (Anthropic Claude), German to Dutch | `<video>.nl.srt` |

A video that comes with subtitles, `<video>.en.vtt` or `<video>.de.vtt` (as yt-dlp writes them,
see [Downloads](#downloads)) or `<video>.en.srt`, has those translated to `<video>.nl.srt`
instead: steps 1 and 2 are not needed. English is taken over German, and WebVTT over SubRip; a
`<video>.nl.vtt` or `<video>.nl.srt` needs no translation at all. A `<video>.de.srt` is taken as
the output of step 1, so it is aligned before it is translated.

A step is skipped when its output file already exists. Each output is written to a temporary file
and renamed when complete, so an interrupted run never leaves a half-written file behind.

## How it works

The engines only do part of the work: Subtitle Edit post-processes Whisper's output, splits
the alignment into batches with its own retiming rules, and merges/splits lines around the
translation calls. To get the same results, this app references Subtitle Edit's own projects and
calls that code directly, re-implementing only what the dialogs do around it.

Translation is the exception. Subtitle Edit's Anthropic engine sends its prompt and the text as
one message and takes the whole reply as the translation, so when Claude corrected itself in the
reply ("Wait, I need to correct the first line"), that ended up in the subtitles. This app calls
Claude itself (`ClaudeTranslator.cs`): it sends the subtitles in numbered batches of up to 30, with
the ones just before and after as context, and a JSON schema holds the reply to one translation
per subtitle. A subtitle that comes back untranslated is asked for again; one that Claude refuses
to translate is kept in the original language, with an error in the output. A subtitle with a
line over the maximum line length (Options > Settings > Rules) is broken again as the "Auto br"
button does it.

It reads the settings, engines and models of the installed Subtitle Edit from
`%APPDATA%\Subtitle Edit` and never writes to them. Change settings in Subtitle Edit itself; the
Claude model, for example, is the one set in Auto-translate (by default `claude-sonnet-5-5`).

Not replicated, so the app stops with an error when they are turned on: an engine other than
Purfview Faster-Whisper-XXL, "Use only front center channel", and in Improve time codes
"Isolate speech first" and "Check with speech-to-text".

## Downloads

A `download.txt` in a folder (searched recursively, like the videos) is worked through first,
downloading into its folder:

```text
https://example.com/video.mp4
https://example.com/video.mp4 Another name
https://example.com/manifest.m3u8 A documentary
```

A line is a URL, optionally followed by whitespace and the file name; without one, the name comes
from the URL. `.mp4` is added when the name has no video extension. A downloaded line gets `# `
in front, and lines starting with `#` are skipped, so a new run continues where the last one left
off. A file that already exists is not downloaded again.

An `.m3u8` URL is downloaded with yt-dlp, in H.264, with the audio in the original version
("multiple languages"), English or German, in that order. Of the subtitles, Dutch, English or
German are downloaded, in that order. Forced subtitles leave out what is spoken in their own
language; English ones are used, as English needs no subtitles, the others are skipped. Anything
else is downloaded directly.

## Setup

Needs git, the .NET 10 SDK, and in Subtitle Edit: Faster-Whisper-XXL with its model, Crisp ASR
with the aligner model, and ffmpeg (all downloaded from Subtitle Edit's own dialogs). For `.m3u8`
downloads, yt-dlp and ffmpeg on the PATH.

```powershell
.\build.ps1
```

This checks out the Subtitle Edit source at the version in `build.ps1` (`$SeTag`) into
`subtitleedit\` and builds. After upgrading Subtitle Edit, update `$SeTag` and run it again.

## Use

```powershell
$env:ANTHROPIC_API_KEY = '...'
.\bin\Release\net10.0\AutoSubtitleGenerator.exe <video file or folder> [...]
```

Folders are searched recursively for video files (`.mp4`, `.mkv`, `.mov`, `.avi`, `.webm`, ...).

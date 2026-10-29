# AutoSubtitleGenerator

Makes German and Dutch subtitles for videos by doing what is otherwise done by hand in
Subtitle Edit, with the same engines, models and settings:

| Step | Subtitle Edit | Output |
|---|---|---|
| 1 | Video > Speech to text (Purfview Faster-Whisper-XXL) | `<video>.de.srt` |
| 2 | Tools > Improve time codes (Crisp ASR forced alignment) | `<video> (aligned).de.srt` |
| 3 | Translate > Auto-translate (DeepL), German to Dutch | `<video>.nl.srt` |

A step is skipped when its output file already exists. Each output is written to a temporary file
and renamed when complete, so an interrupted run never leaves a half-written file behind.

## How it works

The engines only do part of the work: Subtitle Edit post-processes Whisper's output, splits
the alignment into batches with its own retiming rules, and merges/splits lines around the DeepL
calls. To get the same results, this app references Subtitle Edit's own projects and calls that
code directly, re-implementing only what the dialogs do around it.

It reads the settings, engines and models of the installed Subtitle Edit from
`%APPDATA%\Subtitle Edit` and never writes to them. Change settings in Subtitle Edit itself.

Not replicated, so the app stops with an error when they are turned on: an engine other than
Purfview Faster-Whisper-XXL, "Use only front center channel", and in Improve time codes
"Isolate speech first" and "Check with speech-to-text".

## Setup

Needs git, the .NET 10 SDK, and in Subtitle Edit: Faster-Whisper-XXL with its model, Crisp ASR
with the aligner model, and ffmpeg (all downloaded from Subtitle Edit's own dialogs).

```powershell
.\build.ps1
```

This checks out the Subtitle Edit source at the version in `build.ps1` (`$SeTag`) into
`subtitleedit\` and builds. After upgrading Subtitle Edit, update `$SeTag` and run it again.

## Use

```powershell
$env:DEEPL_API_KEY = '...'
.\bin\Release\net10.0\AutoSubtitleGenerator.exe <video file or folder> [...]
```

Folders are searched recursively for video files (`.mp4`, `.mkv`, `.mov`, `.avi`, `.webm`, ...).

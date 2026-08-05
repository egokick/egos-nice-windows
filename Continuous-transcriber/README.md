# Continuous Transcriber

Continuous Transcriber is a fully local Windows microphone transcription service. FFmpeg continuously writes 15-second mono 16 kHz PCM WAV segments, Silero VAD rejects silence and assigns a speech-threshold label, and the CPU-only Parakeet TDT runtime appends recognized English text to UTF-8 transcript files. A single-instance watchdog restarts the worker after a crash or stalled heartbeat.

## Recorder dashboard

Open **Continuous Transcriber → Settings → View dashboard** in the Nice Windows Admin Panel. This starts a separate local dashboard at `http://127.0.0.1:5138/`, adds a **Continuous Transcriber Dashboard** notification-area icon, and opens the dashboard in the default browser. Left-click the icon to reopen the page or use its menu to exit only the dashboard; recording continues independently.

The dashboard joins retained WAV files to transcript lines through each manifest's transcript-line SHA-256, so rotated transcript files and multiple recording sessions appear on one timeline. The range handles filter the transcript, the amber playhead controls the transcript's top position and audio time, transcript rows seek their linked audio, and playback advances automatically across files. **Skip edge silence** omits detected quiet leading and trailing portions without modifying the source WAVs. Search is limited to the selected time range.

For a direct development launch:

```bat
start-dashboard.bat
```

## First run

Requirements:

- Windows 11 and Python 3
- A DirectShow-capable FFmpeg build. The Admin Panel and `prepare-runtime.ps1`
  automatically download, checksum-verify, and keep a private copy when a
  suitable system installation is not already available.
- About 500 MB of free disk space for the pinned runtime and models, plus space for recordings

Run:

```bat
start.bat
```

`start.bat` first runs `prepare-runtime.ps1`, which downloads and SHA-256 verifies the whisper.cpp v1.9.1 Windows x64 runtime, Parakeet TDT 0.6B v3 Q4_K model, and Silero VAD v6.2 model. Native binaries and models live under `runtime\` and are deliberately ignored by Git.

`start.bat` launches the windowless watchdog and returns, which is the launcher used by the Admin Panel and Windows startup. The default microphone is `Microphone Array (Realtek(R) Audio)`. For an interactive monitor whose worker output is visible and which stops on Ctrl+C, run:

```bat
start-console.bat
```

## Audio modes

Default mode deletes every processed WAV:

```bat
start.bat
start.bat --mode default
```

Keep-audio is the normal default for direct, Admin Panel, and startup launches. It retains a WAV only after that chunk produced nonempty text and its transcript line was successfully appended:

```bat
start.bat --mode keep-audio
start.bat --keep-audio
```

To keep no audio after transcription, choose delete mode explicitly:

```bat
start.bat --mode default
```

Silent chunks, empty recognition results, and failed chunks are deleted in both modes. Retained audio moves to `recordings\kept\<session>\` and uses the matching transcript timestamp in a Windows-safe name such as `2026-07-31 09-54-24 rec.wav`. A `(2)` suffix prevents collisions within the same second. The name cannot re-enter the active `chunk_*.wav` queue. `manifest.jsonl` records the corresponding transcript file, full transcript-line SHA-256, and VAD confidence. Changing options requires stopping the running monitor first because the named mutex intentionally rejects a second instance.

In keep-audio mode, shutdown also removes the still-open final segment and every unprocessed exact `chunk_######.wav` left in that session. A new keep-audio worker cleans the same exact names from abandoned prior sessions, covering leftovers from a forced watchdog kill. Default mode preserves the PDF's original final-chunk and old-session behavior.

## Command-line options

`start.bat` forwards all arguments to the monitor, and the monitor forwards behavior arguments unchanged to every worker it starts:

```text
--mic NAME                 DirectShow device display name
--chunk-seconds N          Segment duration, at least 3 (default: 15)
--threads N                Parakeet CPU threads (default: OS CPU count)
--mode default|keep-audio  Audio retention policy
--keep-audio               Alias for --mode keep-audio
--input-file PATH          Loop a file in real time instead of DirectShow
--output PATH              Use one explicit transcript and bypass rotation
--max-chunks N             Exit worker after N completed chunks; 0 is unlimited
```

`--max-chunks` is primarily a direct-worker test hook. Under the normal monitor, a worker that reaches the limit exits and is restarted five seconds later.

The file-input hook follows the same segmentation, VAD, ASR, transcript, and cleanup pipeline:

```bat
python transcribe_microphone.py --input-file sample.wav --max-chunks 2
```

## Transcript and supervision contracts

Transcript lines have this exact shape:

```text
[2026-07-30 23:45:53 Central Summer Time] [speech-confidence >=70%] Recognized text here.
```

The worker reuses the newest modified file matching `transcript YYYY-MM-DD.txt` or `transcript YYYY-MM-DD (N).txt` while the pending UTF-8 line keeps it at or below 512 KiB. Rotation uses the current date, the unsuffixed name when available, and then the first free suffix starting at `(2)`. `--output` bypasses rotation.

The watchdog owns the Windows mutex `Local\ContinuousMicrophoneTranscriberMonitor`, checks the worker and `.transcriber-heartbeat` every five seconds, kills a worker tree when no heartbeat appears within 60 seconds or an existing heartbeat is older than 120 seconds, validates an old recorder PID as `ffmpeg.exe` before killing it, and restarts after five seconds.

## Hidden startup

Run this once to install the current-user Startup shortcut directly to `pythonw.exe`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install-startup.ps1
```

To start in retention mode or remove the shortcut:

```powershell
.\install-startup.ps1 -Mode keep-audio
.\install-startup.ps1 -Remove
```

`start-hidden.vbs` and `start-hidden.bat` are also available for a one-off windowless launch.

## Runtime integrity pins

`prepare-runtime.ps1` installs only these official, immutable artifacts:

| Artifact | Expected bytes | SHA-256 |
| --- | ---: | --- |
| [whisper.cpp v1.9.1 `whisper-bin-x64.zip`](https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip) | 7,982,101 | `7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539` |
| [Parakeet TDT 0.6B v3 Q4_K](https://huggingface.co/ggml-org/parakeet-GGUF) | 415,611,879 | `8b205b8b39c6535e153de6fb11c51db46125d45c4f16ba496fe41a0fe71b885e` |
| [Silero VAD v6.2](https://huggingface.co/ggml-org/whisper-vad) | 885,098 | `2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987` |

Verify an existing installation without downloading or changing it:

```powershell
.\prepare-runtime.ps1 -VerifyOnly
```

## Tests

The application itself and its tests use only the Python standard library:

```bat
python -m unittest discover -s tests -p "test_*.py" -v
python -m py_compile monitor_transcriber.py transcribe_microphone.py
```

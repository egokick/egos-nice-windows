"""Continuously record, gate, transcribe, and persist microphone audio."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import traceback
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Callable, Sequence


APP_DIR = Path(__file__).resolve().parent
RUNTIME_BIN_DIR = APP_DIR / "runtime" / "bin"
MODEL_DIR = APP_DIR / "runtime" / "models"
RECORDINGS_DIR = APP_DIR / "recordings"
CHUNKS_DIR = RECORDINGS_DIR / "chunks"
KEPT_DIR = RECORDINGS_DIR / "kept"
ERROR_LOG = APP_DIR / "transcription-errors.log"
HEARTBEAT_FILE = APP_DIR / ".transcriber-heartbeat"
FFMPEG_PID_FILE = APP_DIR / ".transcriber-ffmpeg.pid"

DEFAULT_MICROPHONE = "Microphone Array (Realtek(R) Audio)"
DEFAULT_CHUNK_SECONDS = 15
DEFAULT_THREADS = max(1, os.cpu_count() or 1)
DEFAULT_PARAKEET_EXE = RUNTIME_BIN_DIR / "parakeet-cli.exe"
DEFAULT_VAD_EXE = RUNTIME_BIN_DIR / "whisper-vad-speech-segments.exe"
DEFAULT_PARAKEET_MODEL = MODEL_DIR / "ggml-parakeet-tdt-0.6b-v3-q4_k.bin"
DEFAULT_VAD_MODEL = MODEL_DIR / "ggml-silero-v6.2.0.bin"
TRANSCRIPT_LIMIT_BYTES = 512 * 1024
HEARTBEAT_INTERVAL_SECONDS = 5.0
QUEUE_POLL_SECONDS = 0.25

CHUNK_NAME_RE = re.compile(r"^chunk_(\d{6})\.wav$", re.IGNORECASE)
TRANSCRIPT_NAME_RE = re.compile(
    r"^transcript \d{4}-\d{2}-\d{2}(?: \((\d+)\))?\.txt$",
    re.IGNORECASE,
)
VAD_RESULT_RE = re.compile(r"Detected\s+(\d+)\s+speech segments?", re.IGNORECASE)


class ChunkProcessingError(RuntimeError):
    """A native VAD or ASR operation failed."""


@dataclass(frozen=True)
class WorkerConfig:
    microphone: str
    chunk_seconds: int
    threads: int
    mode: str
    input_file: Path | None
    output: Path | None
    max_chunks: int
    ffmpeg: str
    parakeet_cli: Path
    vad_cli: Path
    parakeet_model: Path
    vad_model: Path

    @property
    def keep_audio(self) -> bool:
        return self.mode == "keep-audio"


@dataclass(frozen=True)
class ChunkResult:
    transcript_path: Path | None
    retained_audio_path: Path | None
    transcript_line: str | None
    speech_confidence: int | None

    @property
    def wrote_transcript(self) -> bool:
        return self.transcript_path is not None


def add_worker_arguments(parser: argparse.ArgumentParser) -> None:
    """Add worker behavior arguments shared by the worker and monitor."""

    parser.add_argument(
        "--mic",
        default=DEFAULT_MICROPHONE,
        help=f'DirectShow audio device display name (default: "{DEFAULT_MICROPHONE}")',
    )
    parser.add_argument(
        "--chunk-seconds",
        type=int,
        default=DEFAULT_CHUNK_SECONDS,
        help="segment duration in seconds; must be at least 3 (default: 15)",
    )
    parser.add_argument(
        "--threads",
        type=int,
        default=DEFAULT_THREADS,
        help="Parakeet CPU thread count; VAD uses at most 4",
    )
    parser.add_argument(
        "--mode",
        choices=("default", "keep-audio"),
        default="keep-audio",
        help="keep-audio retains transcribed chunks (default); default deletes processed audio",
    )
    parser.add_argument(
        "--keep-audio",
        action="store_true",
        help="alias for --mode keep-audio",
    )
    parser.add_argument(
        "--input-file",
        type=Path,
        help="loop a media file in real time instead of using DirectShow",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="append to one explicit transcript file and bypass size rotation",
    )
    parser.add_argument(
        "--max-chunks",
        type=int,
        default=0,
        help="exit after N completed chunks, including silence (0 means unlimited)",
    )
    parser.add_argument(
        "--ffmpeg",
        default="ffmpeg",
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--parakeet-cli",
        type=Path,
        default=DEFAULT_PARAKEET_EXE,
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--vad-cli",
        type=Path,
        default=DEFAULT_VAD_EXE,
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--parakeet-model",
        type=Path,
        default=DEFAULT_PARAKEET_MODEL,
        help=argparse.SUPPRESS,
    )
    parser.add_argument(
        "--vad-model",
        type=Path,
        default=DEFAULT_VAD_MODEL,
        help=argparse.SUPPRESS,
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    add_worker_arguments(parser)
    return parser


def config_from_namespace(args: argparse.Namespace) -> WorkerConfig:
    mode = "keep-audio" if args.keep_audio else args.mode
    if args.chunk_seconds < 3:
        raise ValueError("--chunk-seconds must be at least 3")
    if args.threads < 1:
        raise ValueError("--threads must be at least 1")
    if args.max_chunks < 0:
        raise ValueError("--max-chunks cannot be negative")
    return WorkerConfig(
        microphone=args.mic,
        chunk_seconds=args.chunk_seconds,
        threads=args.threads,
        mode=mode,
        input_file=args.input_file.resolve() if args.input_file else None,
        output=args.output.resolve() if args.output else None,
        max_chunks=args.max_chunks,
        ffmpeg=args.ffmpeg,
        parakeet_cli=args.parakeet_cli.resolve(),
        vad_cli=args.vad_cli.resolve(),
        parakeet_model=args.parakeet_model.resolve(),
        vad_model=args.vad_model.resolve(),
    )


def worker_arguments(config: WorkerConfig) -> list[str]:
    """Serialize an effective configuration for watchdog child forwarding."""

    arguments = [
        "--mic",
        config.microphone,
        "--chunk-seconds",
        str(config.chunk_seconds),
        "--threads",
        str(config.threads),
        "--mode",
        config.mode,
        "--max-chunks",
        str(config.max_chunks),
        "--ffmpeg",
        config.ffmpeg,
        "--parakeet-cli",
        str(config.parakeet_cli),
        "--vad-cli",
        str(config.vad_cli),
        "--parakeet-model",
        str(config.parakeet_model),
        "--vad-model",
        str(config.vad_model),
    ]
    if config.input_file is not None:
        arguments.extend(("--input-file", str(config.input_file)))
    if config.output is not None:
        arguments.extend(("--output", str(config.output)))
    return arguments


def _creation_flags() -> int:
    return getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0


def _timestamp_prefix(now: datetime | None = None) -> str:
    return (now or datetime.now()).strftime("[%Y-%m-%d %H:%M:%S]")


def log_error(message: str, error_stream=None) -> None:
    line = f"{_timestamp_prefix()} {message.rstrip()}\n"
    if error_stream is not None:
        error_stream.write(line)
        error_stream.flush()
        return
    ERROR_LOG.parent.mkdir(parents=True, exist_ok=True)
    with ERROR_LOG.open("a", encoding="utf-8") as stream:
        stream.write(line)


def validate_runtime(config: WorkerConfig) -> None:
    missing = [
        path
        for path in (
            config.parakeet_cli,
            config.vad_cli,
            config.parakeet_model,
            config.vad_model,
        )
        if not path.is_file()
    ]
    if missing:
        formatted = "\n".join(f"  - {path}" for path in missing)
        raise FileNotFoundError(
            "Required local runtime files are missing. Run prepare-runtime.ps1 first:\n"
            f"{formatted}"
        )
    if config.input_file is not None and not config.input_file.is_file():
        raise FileNotFoundError(f"Input file does not exist: {config.input_file}")
    if not Path(config.ffmpeg).is_file() and shutil.which(config.ffmpeg) is None:
        raise FileNotFoundError(
            f"FFmpeg was not found: {config.ffmpeg!r}. Install FFmpeg with DirectShow support."
        )


def create_session_directory(now: datetime | None = None) -> tuple[str, Path]:
    CHUNKS_DIR.mkdir(parents=True, exist_ok=True)
    base = (now or datetime.now()).strftime("%Y%m%d_%H%M%S")
    session_id = base
    suffix = 2
    while (CHUNKS_DIR / session_id).exists():
        session_id = f"{base}_{suffix}"
        suffix += 1
    session_dir = CHUNKS_DIR / session_id
    session_dir.mkdir()
    return session_id, session_dir


def build_ffmpeg_command(config: WorkerConfig, session_dir: Path) -> list[str]:
    command = [config.ffmpeg, "-hide_banner", "-loglevel", "error"]
    if config.input_file is None:
        command.extend(("-f", "dshow", "-i", f"audio={config.microphone}"))
    else:
        command.extend(("-stream_loop", "-1", "-re", "-i", str(config.input_file)))
    command.extend(
        (
            "-ac",
            "1",
            "-ar",
            "16000",
            "-c:a",
            "pcm_s16le",
            "-f",
            "segment",
            "-segment_time",
            str(config.chunk_seconds),
            "-reset_timestamps",
            "1",
            str(session_dir / "chunk_%06d.wav"),
        )
    )
    return command


def discover_completed_chunks(session_dir: Path) -> list[Path]:
    chunks = sorted(
        (
            path
            for path in session_dir.iterdir()
            if path.is_file() and CHUNK_NAME_RE.fullmatch(path.name)
        ),
        key=lambda path: path.name.lower(),
    )
    return chunks[:-1]


def discard_exact_chunks(session_dir: Path) -> int:
    """Delete only active-queue WAV names from one session directory."""

    if not session_dir.exists():
        return 0
    deleted = 0
    failures: list[str] = []
    for path in session_dir.iterdir():
        if not path.is_file() or CHUNK_NAME_RE.fullmatch(path.name) is None:
            continue
        try:
            path.unlink()
            deleted += 1
        except FileNotFoundError:
            pass
        except OSError as exc:
            failures.append(f"{path}: {exc}")
    if failures:
        raise OSError(
            "Could not discard all untranscribed session audio:\n"
            + "\n".join(failures)
        )
    return deleted


def discard_abandoned_chunks(chunks_root: Path = CHUNKS_DIR) -> int:
    """Clean forced-exit leftovers before a keep-audio worker starts."""

    if not chunks_root.exists():
        return 0
    deleted = 0
    for session_dir in chunks_root.iterdir():
        if session_dir.is_dir() and not session_dir.is_symlink():
            deleted += discard_exact_chunks(session_dir)
    return deleted


def run_vad(
    chunk: Path,
    threshold: float,
    config: WorkerConfig,
    runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> bool:
    command = [
        str(config.vad_cli),
        "-vm",
        str(config.vad_model),
        "-f",
        str(chunk),
        "-t",
        str(min(config.threads, 4)),
        "-vt",
        f"{threshold:.2f}",
        "--vad-min-speech-duration-ms",
        "300",
        "-np",
    ]
    completed = runner(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=_creation_flags(),
        check=False,
    )
    output = completed.stdout or ""
    if completed.returncode != 0:
        raise ChunkProcessingError(
            f"Silero VAD exited with code {completed.returncode} for {chunk.name}:\n"
            f"{output.strip()}"
        )
    match = VAD_RESULT_RE.search(output)
    if match is None:
        raise ChunkProcessingError(
            f"Silero VAD returned an unrecognized response for {chunk.name}:\n"
            f"{output.strip()}"
        )
    return int(match.group(1)) > 0


def speech_confidence(
    chunk: Path,
    config: WorkerConfig,
    runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> int | None:
    if not run_vad(chunk, 0.60, config, runner):
        return None
    for threshold, label in ((0.90, 90), (0.80, 80), (0.70, 70)):
        if run_vad(chunk, threshold, config, runner):
            return label
    return 60


def run_parakeet(
    chunk: Path,
    config: WorkerConfig,
    runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> str:
    command = [
        str(config.parakeet_cli),
        "-m",
        str(config.parakeet_model),
        "-f",
        str(chunk),
        "-t",
        str(config.threads),
        "-ng",
        "-np",
    ]
    completed = runner(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=_creation_flags(),
        check=False,
    )
    if completed.returncode != 0:
        details = (completed.stderr or completed.stdout or "").strip()
        raise ChunkProcessingError(
            f"Parakeet exited with code {completed.returncode} for {chunk.name}:\n{details}"
        )
    return " ".join((completed.stdout or "").split())


def windows_timezone_name(local_time: time.struct_time | None = None) -> str:
    local_time = local_time or time.localtime()
    index = 1 if time.daylight and local_time.tm_isdst > 0 else 0
    if time.tzname and time.tzname[index]:
        return time.tzname[index]
    return "Local Time"


def format_transcript_line(
    text: str,
    confidence: int,
    completed_at: datetime | None = None,
    timezone_name: str | None = None,
) -> str:
    completed_at = completed_at or datetime.now()
    zone = timezone_name or windows_timezone_name()
    timestamp = completed_at.strftime("%Y-%m-%d %H:%M:%S")
    return (
        f"[{timestamp} {zone}] "
        f"[speech-confidence >={confidence}%] {text}\n"
    )


def transcript_files(directory: Path) -> list[Path]:
    if not directory.exists():
        return []
    return [
        path
        for path in directory.iterdir()
        if path.is_file() and TRANSCRIPT_NAME_RE.fullmatch(path.name)
    ]


def next_transcript_path(directory: Path, current_date: str) -> Path:
    unsuffixed = directory / f"transcript {current_date}.txt"
    if not unsuffixed.exists():
        return unsuffixed
    suffix = 2
    while True:
        candidate = directory / f"transcript {current_date} ({suffix}).txt"
        if not candidate.exists():
            return candidate
        suffix += 1


def select_transcript_path(
    directory: Path,
    pending_line_bytes: int,
    completed_at: datetime | None = None,
) -> Path:
    matches = transcript_files(directory)
    if matches:
        newest = max(matches, key=lambda path: path.stat().st_mtime_ns)
        if newest.stat().st_size + pending_line_bytes <= TRANSCRIPT_LIMIT_BYTES:
            return newest
    date_text = (completed_at or datetime.now()).strftime("%Y-%m-%d")
    return next_transcript_path(directory, date_text)


def append_transcript_line(
    line: str,
    output: Path | None = None,
    directory: Path = APP_DIR,
    completed_at: datetime | None = None,
) -> Path:
    data = line.encode("utf-8")
    destination = (
        output
        if output is not None
        else select_transcript_path(directory, len(data), completed_at)
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("ab") as stream:
        stream.write(data)
    return destination


def retained_audio_destination(keep_dir: Path, completed_at: datetime) -> Path:
    # Colons are not valid in Windows filenames, so use hyphens in the time
    # while preserving the transcript timestamp's date and second exactly.
    base_name = completed_at.strftime("%Y-%m-%d %H-%M-%S rec")
    destination = keep_dir / f"{base_name}.wav"
    suffix = 2
    while destination.exists():
        destination = keep_dir / f"{base_name} ({suffix}).wav"
        suffix += 1
    return destination


def retain_audio(
    chunk: Path,
    keep_dir: Path,
    transcript_path: Path,
    transcript_line: str,
    confidence: int,
    completed_at: datetime,
) -> Path:
    keep_dir.mkdir(parents=True, exist_ok=True)
    destination = retained_audio_destination(keep_dir, completed_at)
    chunk.replace(destination)
    manifest_entry = {
        "audio_file": destination.name,
        "transcript_file": str(transcript_path),
        "transcript_line_sha256": hashlib.sha256(
            transcript_line.encode("utf-8")
        ).hexdigest(),
        "speech_confidence": confidence,
    }
    with (keep_dir / "manifest.jsonl").open("a", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(manifest_entry, ensure_ascii=False, sort_keys=True) + "\n")
    return destination


def discard_audio(chunk: Path) -> None:
    try:
        chunk.unlink()
    except FileNotFoundError:
        pass


def process_chunk(
    chunk: Path,
    config: WorkerConfig,
    keep_dir: Path,
    runner: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
    completed_at_factory: Callable[[], datetime] = datetime.now,
) -> ChunkResult:
    """Process one closed chunk and enforce the audio/transcript retention invariant."""

    try:
        confidence = speech_confidence(chunk, config, runner)
        if confidence is None:
            discard_audio(chunk)
            return ChunkResult(None, None, None, None)

        text = run_parakeet(chunk, config, runner)
        if not text:
            discard_audio(chunk)
            return ChunkResult(None, None, None, confidence)

        completed_at = completed_at_factory()
        line = format_transcript_line(text, confidence, completed_at)
        transcript_path = append_transcript_line(
            line,
            output=config.output,
            completed_at=completed_at,
        )
        if config.keep_audio:
            retained_path = retain_audio(
                chunk,
                keep_dir,
                transcript_path,
                line,
                confidence,
                completed_at,
            )
        else:
            discard_audio(chunk)
            retained_path = None
        return ChunkResult(transcript_path, retained_path, line, confidence)
    except BaseException:
        # Failed and untranscribed chunks must never survive in either mode.
        discard_audio(chunk)
        raise


class Heartbeat:
    def __init__(self, path: Path = HEARTBEAT_FILE, interval: float = HEARTBEAT_INTERVAL_SECONDS):
        self.path = path
        self.interval = interval
        self.last_touch = 0.0

    def touch(self, force: bool = False) -> None:
        now = time.monotonic()
        if not force and now - self.last_touch < self.interval:
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.touch()
        self.last_touch = now

    def remove(self) -> None:
        try:
            self.path.unlink()
        except FileNotFoundError:
            pass


def stop_recorder(recorder: subprocess.Popen[bytes] | None) -> None:
    if recorder is None or recorder.poll() is not None:
        return
    recorder.terminate()
    try:
        recorder.wait(timeout=5)
    except subprocess.TimeoutExpired:
        recorder.kill()
        recorder.wait(timeout=5)


def remove_pid_file() -> None:
    try:
        FFMPEG_PID_FILE.unlink()
    except FileNotFoundError:
        pass


def run_worker(config: WorkerConfig, console: bool = True) -> int:
    validate_runtime(config)
    APP_DIR.mkdir(parents=True, exist_ok=True)
    RECORDINGS_DIR.mkdir(parents=True, exist_ok=True)
    if config.keep_audio:
        # A forced watchdog kill cannot run worker cleanup. On the next retained-
        # mode session, remove exact raw chunks that never gained transcripts.
        discard_abandoned_chunks()
    session_id, session_dir = create_session_directory()
    keep_dir = KEPT_DIR / session_id
    heartbeat = Heartbeat()
    recorder: subprocess.Popen[bytes] | None = None
    processed_count = 0

    with ERROR_LOG.open("a", encoding="utf-8") as error_stream:
        try:
            heartbeat.touch(force=True)
            recorder = subprocess.Popen(
                build_ffmpeg_command(config, session_dir),
                cwd=APP_DIR,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=error_stream,
                creationflags=_creation_flags(),
            )
            FFMPEG_PID_FILE.write_text(str(recorder.pid), encoding="ascii")
            if console:
                print(
                    f"Continuous Transcriber is running (session {session_id}, "
                    f"mode {config.mode}). Press Ctrl+C to stop.",
                    flush=True,
                )

            stop_requested = False
            while recorder.poll() is None and not stop_requested:
                heartbeat.touch()
                for chunk in discover_completed_chunks(session_dir):
                    heartbeat.touch()
                    try:
                        result = process_chunk(chunk, config, keep_dir)
                    except Exception as exc:
                        log_error(str(exc), error_stream)
                        raise
                    processed_count += 1
                    if console and result.transcript_line:
                        print(result.transcript_line, end="", flush=True)
                    heartbeat.touch()
                    if config.max_chunks and processed_count >= config.max_chunks:
                        stop_requested = True
                        break
                if not stop_requested:
                    time.sleep(QUEUE_POLL_SECONDS)

            if stop_requested:
                return 0
            return_code = recorder.poll()
            raise RuntimeError(f"FFmpeg exited unexpectedly with code {return_code}")
        finally:
            stop_recorder(recorder)
            if config.keep_audio:
                try:
                    # This includes the still-open final segment and any backlog
                    # not processed before Ctrl+C, a native error, or max-chunks.
                    discard_exact_chunks(session_dir)
                except OSError as exc:
                    log_error(str(exc), error_stream)
            remove_pid_file()
            heartbeat.remove()


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        config = config_from_namespace(args)
    except ValueError as exc:
        parser.error(str(exc))

    try:
        return run_worker(config)
    except KeyboardInterrupt:
        print("\nContinuous Transcriber stopped.")
        return 0
    except Exception:
        details = traceback.format_exc()
        log_error(details)
        print(
            "Continuous Transcriber stopped after an error. "
            f"See {ERROR_LOG}.",
            file=sys.stderr,
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

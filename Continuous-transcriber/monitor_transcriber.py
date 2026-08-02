"""Single-instance watchdog for the continuous microphone transcriber."""

from __future__ import annotations

import argparse
import csv
import ctypes
import os
import signal
import subprocess
import sys
import time
import traceback
from datetime import datetime
from pathlib import Path
from typing import Sequence

from transcribe_microphone import (
    APP_DIR,
    ERROR_LOG,
    FFMPEG_PID_FILE,
    HEARTBEAT_FILE,
    add_worker_arguments,
    config_from_namespace,
    worker_arguments,
)


MUTEX_NAME = r"Local\ContinuousMicrophoneTranscriberMonitor"
ERROR_ALREADY_EXISTS = 183
POLL_SECONDS = 5.0
INITIAL_HEARTBEAT_TIMEOUT_SECONDS = 60.0
STALE_HEARTBEAT_SECONDS = 120.0
RESTART_DELAY_SECONDS = 5.0
WORKER_SCRIPT = APP_DIR / "transcribe_microphone.py"


class NamedMutex:
    def __init__(self, name: str = MUTEX_NAME):
        self.name = name
        self.handle: int | None = None
        self.already_exists = False

    def acquire(self) -> bool:
        if os.name != "nt":
            raise OSError("The Continuous Transcriber watchdog requires Windows")
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateMutexW.argtypes = (ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p)
        kernel32.CreateMutexW.restype = ctypes.c_void_p
        ctypes.set_last_error(0)
        handle = kernel32.CreateMutexW(None, False, self.name)
        if not handle:
            raise ctypes.WinError(ctypes.get_last_error())
        self.handle = int(handle)
        self.already_exists = ctypes.get_last_error() == ERROR_ALREADY_EXISTS
        return not self.already_exists

    def close(self) -> None:
        if self.handle is None:
            return
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CloseHandle.argtypes = (ctypes.c_void_p,)
        kernel32.CloseHandle.restype = ctypes.c_bool
        kernel32.CloseHandle(self.handle)
        self.handle = None

    def __enter__(self) -> "NamedMutex":
        self.acquire()
        return self

    def __exit__(self, *_unused: object) -> None:
        self.close()


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--console",
        action="store_true",
        help="show status and inherit worker output for interactive use",
    )
    add_worker_arguments(parser)
    return parser


def _timestamp() -> str:
    return datetime.now().strftime("[%Y-%m-%d %H:%M:%S]")


def monitor_log(message: str, console: bool = False) -> None:
    line = f"{_timestamp()} {message.rstrip()}"
    ERROR_LOG.parent.mkdir(parents=True, exist_ok=True)
    with ERROR_LOG.open("a", encoding="utf-8") as stream:
        stream.write(line + "\n")
    if console:
        print(line, flush=True)


def read_recorder_pid(path: Path = FFMPEG_PID_FILE) -> int | None:
    try:
        value = int(path.read_text(encoding="ascii").strip())
    except (OSError, UnicodeError, ValueError):
        return None
    return value if value > 0 else None


def image_name_for_pid(pid: int) -> str | None:
    if os.name != "nt":
        return None
    completed = subprocess.run(
        ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        check=False,
    )
    if completed.returncode != 0 or not completed.stdout.strip():
        return None
    try:
        row = next(csv.reader([completed.stdout.splitlines()[0]]))
    except (csv.Error, StopIteration):
        return None
    if not row or row[0].startswith("INFO:"):
        return None
    return row[0]


def terminate_process_tree(pid: int, console: bool = False) -> None:
    if pid <= 0:
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            stdout=None if console else subprocess.DEVNULL,
            stderr=None if console else subprocess.DEVNULL,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            check=False,
        )
        return
    try:
        os.kill(pid, signal.SIGTERM)
    except ProcessLookupError:
        pass


def _unlink(path: Path) -> None:
    try:
        path.unlink()
    except FileNotFoundError:
        pass


def cleanup_stale_recorder(console: bool = False) -> None:
    pid = read_recorder_pid()
    if pid is not None and (image_name_for_pid(pid) or "").casefold() == "ffmpeg.exe":
        monitor_log(f"Stopping stale ffmpeg.exe process {pid}.", console)
        terminate_process_tree(pid, console)
    _unlink(FFMPEG_PID_FILE)


def clear_worker_state(console: bool = False) -> None:
    cleanup_stale_recorder(console)
    _unlink(HEARTBEAT_FILE)


def heartbeat_failure(
    launched_at: float,
    now: float | None = None,
    heartbeat_path: Path = HEARTBEAT_FILE,
) -> str | None:
    now = now if now is not None else time.time()
    if not heartbeat_path.exists():
        if now - launched_at >= INITIAL_HEARTBEAT_TIMEOUT_SECONDS:
            return "heartbeat did not appear within 60 seconds"
        return None
    try:
        age = now - heartbeat_path.stat().st_mtime
    except FileNotFoundError:
        return None
    if age > STALE_HEARTBEAT_SECONDS:
        return f"heartbeat is stale ({age:.0f} seconds old)"
    return None


def launch_worker(
    forwarded_arguments: Sequence[str],
    console: bool,
) -> tuple[subprocess.Popen[bytes], object | None]:
    command = [sys.executable, str(WORKER_SCRIPT), *forwarded_arguments]
    if console:
        return (
            subprocess.Popen(command, cwd=APP_DIR),
            None,
        )
    error_stream = ERROR_LOG.open("a", encoding="utf-8")
    try:
        child = subprocess.Popen(
            command,
            cwd=APP_DIR,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=error_stream,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)
            if os.name == "nt"
            else 0,
        )
    except BaseException:
        error_stream.close()
        raise
    return child, error_stream


def wait_for_child(child: subprocess.Popen[bytes], console: bool) -> int:
    launched_at = time.time()
    while True:
        return_code = child.poll()
        if return_code is not None:
            return return_code
        failure = heartbeat_failure(launched_at)
        if failure is not None:
            monitor_log(
                f"Worker {child.pid} is unhealthy: {failure}; terminating its process tree.",
                console,
            )
            terminate_process_tree(child.pid, console)
            try:
                return child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                return -1
        time.sleep(POLL_SECONDS)


def run_watchdog(forwarded_arguments: Sequence[str], console: bool) -> int:
    child: subprocess.Popen[bytes] | None = None
    error_stream: object | None = None
    try:
        while True:
            clear_worker_state(console)
            child, error_stream = launch_worker(forwarded_arguments, console)
            monitor_log(f"Started transcriber worker {child.pid}.", console)
            return_code = wait_for_child(child, console)
            if error_stream is not None:
                error_stream.close()
                error_stream = None
            monitor_log(
                f"Transcriber worker {child.pid} exited with code {return_code}.",
                console,
            )
            clear_worker_state(console)
            if console:
                print("Restarting in 5 seconds...", flush=True)
            time.sleep(RESTART_DELAY_SECONDS)
    except KeyboardInterrupt:
        if console:
            print("\nStopping Continuous Transcriber...", flush=True)
        return 0
    finally:
        if child is not None and child.poll() is None:
            terminate_process_tree(child.pid, console)
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                pass
        if error_stream is not None:
            error_stream.close()
        clear_worker_state(console)


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        config = config_from_namespace(args)
    except ValueError as exc:
        parser.error(str(exc))
    forwarded_arguments = worker_arguments(config)

    mutex = NamedMutex()
    try:
        if not mutex.acquire():
            if args.console:
                print("Continuous Transcriber is already running.")
            return 0
        return run_watchdog(forwarded_arguments, args.console)
    except Exception:
        details = traceback.format_exc()
        monitor_log(details, args.console)
        return 1
    finally:
        mutex.close()


if __name__ == "__main__":
    raise SystemExit(main())

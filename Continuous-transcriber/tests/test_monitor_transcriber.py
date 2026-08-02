from __future__ import annotations

import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


APP_DIRECTORY = Path(__file__).resolve().parents[1]
if str(APP_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(APP_DIRECTORY))

import monitor_transcriber as monitor
import transcribe_microphone as worker


class MonitorContractTests(unittest.TestCase):
    def test_mutex_name_is_exact(self):
        self.assertEqual(
            monitor.MUTEX_NAME,
            r"Local\ContinuousMicrophoneTranscriberMonitor",
        )

    def test_monitor_parses_and_forwards_worker_behavior(self):
        args = monitor.build_parser().parse_args(
            [
                "--console",
                "--mic",
                "USB Mic",
                "--chunk-seconds",
                "20",
                "--threads",
                "3",
                "--keep-audio",
                "--max-chunks",
                "2",
            ]
        )
        config = worker.config_from_namespace(args)
        forwarded = worker.worker_arguments(config)
        child_config = worker.config_from_namespace(
            worker.build_parser().parse_args(forwarded)
        )

        self.assertTrue(args.console)
        self.assertEqual(child_config, config)
        self.assertNotIn("--console", forwarded)
        self.assertEqual(child_config.mode, "keep-audio")

    def test_missing_heartbeat_gets_60_second_grace(self):
        with tempfile.TemporaryDirectory() as temporary:
            heartbeat = Path(temporary) / "heartbeat"
            self.assertIsNone(
                monitor.heartbeat_failure(100.0, now=159.9, heartbeat_path=heartbeat)
            )
            self.assertEqual(
                monitor.heartbeat_failure(100.0, now=160.0, heartbeat_path=heartbeat),
                "heartbeat did not appear within 60 seconds",
            )

    def test_existing_heartbeat_is_stale_only_after_120_seconds(self):
        with tempfile.TemporaryDirectory() as temporary:
            heartbeat = Path(temporary) / "heartbeat"
            heartbeat.touch()
            monitor.os.utime(heartbeat, (100.0, 100.0))

            self.assertIsNone(
                monitor.heartbeat_failure(0.0, now=220.0, heartbeat_path=heartbeat)
            )
            reason = monitor.heartbeat_failure(
                0.0, now=220.1, heartbeat_path=heartbeat
            )
            self.assertIsNotNone(reason)
            self.assertIn("heartbeat is stale", reason)

    def test_pid_reader_rejects_invalid_values(self):
        with tempfile.TemporaryDirectory() as temporary:
            pid_file = Path(temporary) / "pid"
            for value in ("", "not-a-number", "0", "-1"):
                with self.subTest(value=value):
                    pid_file.write_text(value, encoding="ascii")
                    self.assertIsNone(monitor.read_recorder_pid(pid_file))
            pid_file.write_text("1234", encoding="ascii")
            self.assertEqual(monitor.read_recorder_pid(pid_file), 1234)

    def test_tasklist_csv_parser_extracts_image_name(self):
        completed = subprocess.CompletedProcess(
            [],
            0,
            stdout='"ffmpeg.exe","1234","Console","1","12,340 K"\n',
        )
        with (
            mock.patch.object(monitor.os, "name", "nt"),
            mock.patch.object(monitor.subprocess, "run", return_value=completed),
        ):
            self.assertEqual(monitor.image_name_for_pid(1234), "ffmpeg.exe")

    def test_stale_cleanup_only_kills_validated_ffmpeg_pid(self):
        with (
            mock.patch.object(monitor, "read_recorder_pid", return_value=1234),
            mock.patch.object(monitor, "image_name_for_pid", return_value="python.exe"),
            mock.patch.object(monitor, "terminate_process_tree") as terminate,
            mock.patch.object(monitor, "_unlink"),
        ):
            monitor.cleanup_stale_recorder()
            terminate.assert_not_called()

        with (
            mock.patch.object(monitor, "read_recorder_pid", return_value=1234),
            mock.patch.object(monitor, "image_name_for_pid", return_value="FFMPEG.EXE"),
            mock.patch.object(monitor, "terminate_process_tree") as terminate,
            mock.patch.object(monitor, "_unlink"),
            mock.patch.object(monitor, "monitor_log"),
        ):
            monitor.cleanup_stale_recorder()
            terminate.assert_called_once_with(1234, False)

    def test_console_launch_uses_same_python_and_argument_list(self):
        fake_child = mock.Mock()
        with mock.patch.object(
            monitor.subprocess, "Popen", return_value=fake_child
        ) as popen:
            child, stream = monitor.launch_worker(
                ["--mode", "keep-audio", "--mic", "USB Mic"], True
            )

        self.assertIs(child, fake_child)
        self.assertIsNone(stream)
        command = popen.call_args.args[0]
        self.assertEqual(command[0], monitor.sys.executable)
        self.assertEqual(Path(command[1]), monitor.WORKER_SCRIPT)
        self.assertEqual(
            command[2:],
            ["--mode", "keep-audio", "--mic", "USB Mic"],
        )
        self.assertEqual(popen.call_args.kwargs["cwd"], monitor.APP_DIR)

    def test_start_launcher_is_detached_venv_aware_and_forwards_arguments(self):
        content = (APP_DIRECTORY / "start.bat").read_text(encoding="utf-8")
        self.assertIn(".venv\\Scripts\\pythonw.exe", content)
        self.assertIn('start "" /b', content)
        self.assertIn('"%APP_DIR%\\monitor_transcriber.py" %*', content)
        self.assertNotIn("--console", content)

    def test_console_launcher_preserves_interactive_contract(self):
        content = (APP_DIRECTORY / "start-console.bat").read_text(encoding="utf-8")
        self.assertIn(
            '"%APP_DIR%\\monitor_transcriber.py" --console %*',
            content,
        )

    def test_runtime_bootstrap_contains_immutable_pins(self):
        content = (APP_DIRECTORY / "prepare-runtime.ps1").read_text(
            encoding="utf-8"
        )
        for required in (
            "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip",
            "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539",
            "415611879",
            "8b205b8b39c6535e153de6fb11c51db46125d45c4f16ba496fe41a0fe71b885e",
            "885098",
            "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",
        ):
            with self.subTest(required=required):
                self.assertIn(required, content)


if __name__ == "__main__":
    unittest.main()

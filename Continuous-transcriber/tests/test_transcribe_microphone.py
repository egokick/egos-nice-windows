from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime
from pathlib import Path
from unittest import mock


APP_DIRECTORY = Path(__file__).resolve().parents[1]
if str(APP_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(APP_DIRECTORY))

import transcribe_microphone as worker


def make_config(root: Path, *, mode: str = "default", output: Path | None = None):
    return worker.WorkerConfig(
        microphone="Test Microphone",
        chunk_seconds=15,
        threads=8,
        mode=mode,
        input_file=None,
        output=output,
        max_chunks=0,
        ffmpeg="ffmpeg",
        parakeet_cli=root / "parakeet-cli.exe",
        vad_cli=root / "whisper-vad-speech-segments.exe",
        parakeet_model=root / "parakeet.bin",
        vad_model=root / "silero.bin",
    )


class CliAndCommandTests(unittest.TestCase):
    def test_defaults_match_specification(self):
        with mock.patch.object(worker, "default_microphone_name", return_value="Default Mic"):
            args = worker.build_parser().parse_args([])
            config = worker.config_from_namespace(args)

        self.assertEqual(config.microphone, "Default Mic")
        self.assertEqual(config.chunk_seconds, 15)
        self.assertEqual(config.threads, max(1, worker.os.cpu_count() or 1))
        self.assertEqual(config.mode, "keep-audio")
        self.assertEqual(config.max_chunks, 0)

    def test_keep_audio_alias_overrides_default_mode(self):
        args = worker.build_parser().parse_args(["--mode", "default", "--keep-audio"])
        self.assertEqual(worker.config_from_namespace(args).mode, "keep-audio")

    def test_configuration_rejects_bad_numeric_values(self):
        for arguments in (
            ["--chunk-seconds", "2"],
            ["--threads", "0"],
            ["--max-chunks", "-1"],
        ):
            with self.subTest(arguments=arguments):
                with self.assertRaises(ValueError):
                    worker.config_from_namespace(worker.build_parser().parse_args(arguments))

    def test_default_ffmpeg_command_is_directshow_pcm_segmentation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            command = worker.build_ffmpeg_command(make_config(root), root / "session")

        self.assertEqual(
            command,
            [
                "ffmpeg",
                "-hide_banner",
                "-loglevel",
                "error",
                "-f",
                "dshow",
                "-i",
                "audio=Test Microphone",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-c:a",
                "pcm_s16le",
                "-f",
                "segment",
                "-segment_time",
                "15",
                "-reset_timestamps",
                "1",
                str(root / "session" / "chunk_%06d.wav"),
            ],
        )

    def test_file_input_loops_in_real_time(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = make_config(root)
            config = worker.WorkerConfig(
                **{**config.__dict__, "input_file": root / "sample.wav"}
            )
            command = worker.build_ffmpeg_command(config, root / "session")

        index = command.index("-stream_loop")
        self.assertEqual(
            command[index : index + 6],
            ["-stream_loop", "-1", "-re", "-i", str(root / "sample.wav"), "-ac"],
        )

    def test_forwarded_arguments_preserve_effective_behavior(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = make_config(root, mode="keep-audio", output=root / "out.txt")
            config = worker.WorkerConfig(
                **{
                    **config.__dict__,
                    "microphone": "USB Microphone",
                    "chunk_seconds": 30,
                    "input_file": root / "input.wav",
                    "max_chunks": 4,
                }
            )
            reparsed = worker.config_from_namespace(
                worker.build_parser().parse_args(worker.worker_arguments(config))
            )
        self.assertEqual(reparsed, config)


class NativeCommandTests(unittest.TestCase):
    def test_vad_command_caps_threads_and_parses_segment_count(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = make_config(root)
            observed = []

            def runner(command, **kwargs):
                observed.append((command, kwargs))
                return subprocess.CompletedProcess(
                    command, 0, stdout="Detected 2 speech segments\n"
                )

            self.assertTrue(worker.run_vad(root / "chunk_000000.wav", 0.60, config, runner))

        command, kwargs = observed[0]
        self.assertEqual(command[command.index("-t") + 1], "4")
        self.assertEqual(command[command.index("-vt") + 1], "0.60")
        self.assertIn("--vad-min-speech-duration-ms", command)
        self.assertEqual(command[-1], "-np")
        self.assertEqual(kwargs["stderr"], subprocess.STDOUT)

    def test_confidence_tests_thresholds_in_required_order(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = make_config(root)
            thresholds = []

            def runner(command, **_kwargs):
                threshold = command[command.index("-vt") + 1]
                thresholds.append(threshold)
                count = 1 if threshold in {"0.60", "0.70"} else 0
                return subprocess.CompletedProcess(
                    command, 0, stdout=f"Detected {count} speech segments"
                )

            confidence = worker.speech_confidence(
                root / "chunk_000000.wav", config, runner
            )

        self.assertEqual(confidence, 70)
        self.assertEqual(thresholds, ["0.60", "0.90", "0.80", "0.70"])

    def test_silent_gate_does_not_run_later_thresholds(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = make_config(root)
            calls = []

            def runner(command, **_kwargs):
                calls.append(command)
                return subprocess.CompletedProcess(
                    command, 0, stdout="Detected 0 speech segments"
                )

            self.assertIsNone(
                worker.speech_confidence(root / "chunk_000000.wav", config, runner)
            )
        self.assertEqual(len(calls), 1)

    def test_unrecognized_vad_output_is_fatal(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = make_config(root)

            def runner(command, **_kwargs):
                return subprocess.CompletedProcess(command, 0, stdout="unexpected")

            with self.assertRaises(worker.ChunkProcessingError):
                worker.run_vad(root / "chunk_000000.wav", 0.60, config, runner)

    def test_parakeet_is_cpu_only_and_collapses_whitespace(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = make_config(root)
            observed = []

            def runner(command, **kwargs):
                observed.append((command, kwargs))
                return subprocess.CompletedProcess(
                    command, 0, stdout="  Hello,\n  local   world. \n", stderr=""
                )

            result = worker.run_parakeet(root / "chunk_000000.wav", config, runner)

        self.assertEqual(result, "Hello, local world.")
        command, _kwargs = observed[0]
        self.assertEqual(command[-2:], ["-ng", "-np"])
        self.assertEqual(command[command.index("-t") + 1], "8")


class QueueAndTranscriptTests(unittest.TestCase):
    def test_discovery_is_exact_and_excludes_final_open_chunk(self):
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            for name in (
                "chunk_000002.wav",
                "chunk_000000.wav",
                "chunk_000001.wav",
                "chunk_000001.processed.wav",
                "chunk_bad.wav",
                "retained_000003.wav",
            ):
                (session / name).write_bytes(b"audio")
            completed = worker.discover_completed_chunks(session)

        self.assertEqual(
            [path.name for path in completed],
            ["chunk_000000.wav", "chunk_000001.wav"],
        )

    def test_keep_mode_cleanup_deletes_only_exact_raw_chunks(self):
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            raw = session / "chunk_000000.wav"
            final = session / "chunk_000001.wav"
            processed_name = session / "chunk_000000.processed.wav"
            retained_name = session / "retained_000000.wav"
            for path in (raw, final, processed_name, retained_name):
                path.write_bytes(b"audio")

            deleted = worker.discard_exact_chunks(session)

            self.assertEqual(deleted, 2)
            self.assertFalse(raw.exists())
            self.assertFalse(final.exists())
            self.assertTrue(processed_name.exists())
            self.assertTrue(retained_name.exists())

    def test_keep_mode_startup_cleanup_covers_abandoned_sessions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for session_name in ("20260731_120000", "20260731_120005"):
                session = root / session_name
                session.mkdir()
                (session / "chunk_000000.wav").write_bytes(b"orphan")
                (session / "notes.txt").write_text("leave me", encoding="utf-8")

            deleted = worker.discard_abandoned_chunks(root)

            self.assertEqual(deleted, 2)
            for session in root.iterdir():
                self.assertFalse((session / "chunk_000000.wav").exists())
                self.assertTrue((session / "notes.txt").exists())

    def test_transcript_line_has_exact_format(self):
        line = worker.format_transcript_line(
            "Recognized text here.",
            70,
            datetime(2026, 7, 30, 23, 45, 53),
            "Central Summer Time",
        )
        self.assertEqual(
            line,
            "[2026-07-30 23:45:53 Central Summer Time] "
            "[speech-confidence >=70%] Recognized text here.\n",
        )

    def test_rotation_appends_at_exact_limit_and_rotates_over_limit(self):
        line = "new line\n"
        line_size = len(line.encode("utf-8"))
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            existing = root / "transcript 2026-07-30.txt"
            with existing.open("wb") as stream:
                stream.truncate(worker.TRANSCRIPT_LIMIT_BYTES - line_size)

            selected = worker.select_transcript_path(
                root, line_size, datetime(2026, 7, 31)
            )
            self.assertEqual(selected, existing)

            with existing.open("ab") as stream:
                stream.write(b"x")
            rotated = worker.select_transcript_path(
                root, line_size, datetime(2026, 7, 31)
            )
            self.assertEqual(rotated.name, "transcript 2026-07-31.txt")

    def test_rotation_reuses_newest_file_across_dates(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            older = root / "transcript 2026-07-30.txt"
            newer = root / "transcript 2026-07-29 (2).txt"
            older.write_text("older\n", encoding="utf-8")
            newer.write_text("newer\n", encoding="utf-8")
            worker.os.utime(older, (100, 100))
            worker.os.utime(newer, (200, 200))

            selected = worker.select_transcript_path(
                root, 10, datetime(2026, 7, 31)
            )
        self.assertEqual(selected, newer)

    def test_new_rotation_suffix_starts_at_two(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "transcript 2026-07-31.txt").write_text("", encoding="utf-8")
            (root / "transcript 2026-07-31 (2).txt").write_text("", encoding="utf-8")
            path = worker.next_transcript_path(root, "2026-07-31")
        self.assertEqual(path.name, "transcript 2026-07-31 (3).txt")


class RetentionPolicyTests(unittest.TestCase):
    def test_silence_is_deleted_in_keep_audio_mode(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chunk = root / "chunk_000000.wav"
            chunk.write_bytes(b"silent")
            config = make_config(root, mode="keep-audio", output=root / "out.txt")
            with mock.patch.object(worker, "speech_confidence", return_value=None):
                result = worker.process_chunk(chunk, config, root / "kept")

            self.assertFalse(chunk.exists())
            self.assertFalse((root / "kept").exists())
            self.assertFalse(result.wrote_transcript)

    def test_empty_asr_result_is_deleted_in_keep_audio_mode(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chunk = root / "chunk_000000.wav"
            chunk.write_bytes(b"speech")
            config = make_config(root, mode="keep-audio", output=root / "out.txt")
            with (
                mock.patch.object(worker, "speech_confidence", return_value=80),
                mock.patch.object(worker, "run_parakeet", return_value=""),
            ):
                result = worker.process_chunk(chunk, config, root / "kept")

            self.assertFalse(chunk.exists())
            self.assertFalse((root / "kept").exists())
            self.assertFalse(result.wrote_transcript)

    def test_default_mode_appends_then_deletes_audio(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chunk = root / "chunk_000000.wav"
            chunk.write_bytes(b"speech")
            output = root / "out.txt"
            config = make_config(root, output=output)
            with (
                mock.patch.object(worker, "speech_confidence", return_value=90),
                mock.patch.object(worker, "run_parakeet", return_value="Hello."),
                mock.patch.object(
                    worker, "windows_timezone_name", return_value="Central Summer Time"
                ),
            ):
                result = worker.process_chunk(
                    chunk,
                    config,
                    root / "kept",
                    completed_at_factory=lambda: datetime(2026, 7, 31, 12, 0, 0),
                )

            self.assertTrue(result.wrote_transcript)
            self.assertFalse(chunk.exists())
            self.assertFalse((root / "kept").exists())
            self.assertEqual(
                output.read_text(encoding="utf-8"),
                "[2026-07-31 12:00:00 Central Summer Time] "
                "[speech-confidence >=90%] Hello.\n",
            )

    def test_keep_audio_requires_and_records_persisted_transcript(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chunk = root / "chunk_000007.wav"
            original_audio = b"speech bytes"
            chunk.write_bytes(original_audio)
            output = root / "out.txt"
            keep_dir = root / "kept"
            config = make_config(root, mode="keep-audio", output=output)
            with (
                mock.patch.object(worker, "speech_confidence", return_value=70),
                mock.patch.object(worker, "run_parakeet", return_value="Kept text."),
                mock.patch.object(
                    worker, "windows_timezone_name", return_value="Central Summer Time"
                ),
            ):
                result = worker.process_chunk(
                    chunk,
                    config,
                    keep_dir,
                    completed_at_factory=lambda: datetime(2026, 7, 31, 12, 0, 1),
                )

            self.assertFalse(chunk.exists())
            self.assertIsNotNone(result.retained_audio_path)
            retained = result.retained_audio_path
            assert retained is not None
            self.assertEqual(retained.read_bytes(), original_audio)
            self.assertEqual(retained.name, "2026-07-31 12-00-01 rec.wav")
            self.assertIsNone(worker.CHUNK_NAME_RE.fullmatch(retained.name))
            line = output.read_text(encoding="utf-8")
            expected_digest = hashlib.sha256(line.encode("utf-8")).hexdigest()
            manifest = json.loads(
                (keep_dir / "manifest.jsonl").read_text(encoding="utf-8").strip()
            )
            self.assertEqual(manifest["transcript_line_sha256"], expected_digest)
            self.assertEqual(manifest["audio_file"], retained.name)
            self.assertEqual(Path(manifest["transcript_file"]), output)

    def test_retained_audio_name_uses_collision_suffix(self):
        with tempfile.TemporaryDirectory() as temporary:
            keep_dir = Path(temporary)
            completed_at = datetime(2026, 7, 31, 12, 0, 1)
            (keep_dir / "2026-07-31 12-00-01 rec.wav").write_bytes(b"first")

            destination = worker.retained_audio_destination(keep_dir, completed_at)

            self.assertEqual(
                destination.name, "2026-07-31 12-00-01 rec (2).wav"
            )

    def test_failed_transcript_append_discards_audio(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chunk = root / "chunk_000000.wav"
            chunk.write_bytes(b"speech")
            config = make_config(root, mode="keep-audio", output=root / "out.txt")
            with (
                mock.patch.object(worker, "speech_confidence", return_value=70),
                mock.patch.object(worker, "run_parakeet", return_value="Text."),
                mock.patch.object(
                    worker,
                    "append_transcript_line",
                    side_effect=OSError("disk full"),
                ),
            ):
                with self.assertRaises(OSError):
                    worker.process_chunk(chunk, config, root / "kept")

            self.assertFalse(chunk.exists())
            self.assertFalse((root / "kept").exists())

    def test_default_mode_does_not_enable_session_cleanup(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            chunk = root / "chunk_000000.wav"
            chunk.write_bytes(b"open final chunk")
            config = make_config(root, mode="default")

            if config.keep_audio:
                worker.discard_exact_chunks(root)

            self.assertTrue(chunk.exists())


if __name__ == "__main__":
    unittest.main()

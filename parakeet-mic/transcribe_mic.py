"""Transcribe completed microphone utterances with NVIDIA Parakeet TDT v2."""

from __future__ import annotations

import argparse
import queue
import signal
import sys
import time

import numpy as np
import sounddevice as sd
import torch

# NeMo 2.0 references SIGKILL only as the default for an optional Linux fault-
# tolerance feature. Windows has no SIGKILL, so supply the nearest termination
# signal before importing NeMo. It does not affect transcription.
if not hasattr(signal, "SIGKILL"):
    signal.SIGKILL = signal.SIGTERM

import nemo.collections.asr as nemo_asr


MODEL_ID = "nvidia/parakeet-tdt-0.6b-v2"
SAMPLE_RATE = 16_000


def audio_devices() -> str:
    return str(sd.query_devices())


def rms(samples: np.ndarray) -> float:
    return float(np.sqrt(np.mean(np.square(samples), dtype=np.float64))) if samples.size else 0.0


def format_result(result: object) -> str:
    """Cope with NeMo versions that return either strings or Hypothesis objects."""
    while isinstance(result, (list, tuple)) and len(result) == 1:
        result = result[0]
    if isinstance(result, str):
        return result.strip()
    return str(getattr(result, "text", result)).strip()


def transcribe(model: object, samples: np.ndarray) -> str:
    # Parakeet expects 16 kHz mono audio.  Passing a NumPy array avoids temporary
    # WAV files and keeps each completed utterance entirely in memory.
    with torch.inference_mode():
        result = model.transcribe(audio=[samples.astype(np.float32)], batch_size=1)
    return format_result(result[0]) if result else ""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--list-devices", action="store_true", help="print audio devices and exit")
    parser.add_argument("--device", default=None, help="sounddevice input device name or number")
    parser.add_argument("--threshold", type=float, default=0.008, help="speech RMS threshold (default: 0.008)")
    parser.add_argument("--silence-ms", type=int, default=700, help="silence ending an utterance (default: 700)")
    parser.add_argument("--max-seconds", type=float, default=15.0, help="maximum utterance length (default: 15)")
    parser.add_argument("--test-microphone", action="store_true", help="open the microphone for one block, report its RMS, and exit")
    args = parser.parse_args()

    if args.list_devices:
        print(audio_devices())
        return 0
    if not torch.cuda.is_available():
        print("CUDA GPU unavailable; this app is configured to require NVIDIA GPU acceleration.", file=sys.stderr)
        return 2

    gpu = torch.cuda.get_device_name(0)
    print("Loading Parakeet TDT v2 onto the NVIDIA GPU…", flush=True)
    model = nemo_asr.models.ASRModel.from_pretrained(MODEL_ID)
    model = model.cuda().eval()

    block_ms = 100
    block_size = SAMPLE_RATE * block_ms // 1000
    silence_blocks_to_finish = max(1, round(args.silence_ms / block_ms))
    max_blocks = max(1, round(args.max_seconds * 1000 / block_ms))
    audio_queue: queue.Queue[np.ndarray] = queue.Queue()
    input_device: int | str | None = int(args.device) if args.device and args.device.isdigit() else args.device

    def callback(indata: np.ndarray, frames: int, time_info: object, status: sd.CallbackFlags) -> None:
        if status:
            print(f"Audio warning: {status}", file=sys.stderr)
        audio_queue.put(indata[:, 0].copy())

    if args.test_microphone:
        with sd.InputStream(
            samplerate=SAMPLE_RATE,
            blocksize=block_size,
            device=input_device,
            channels=1,
            dtype="float32",
            callback=callback,
        ):
            captured = audio_queue.get(timeout=3)
        print(f"Microphone check passed: {len(captured)} samples, RMS {rms(captured):.5f}")
        return 0

    selected_device = "default" if input_device is None else str(input_device)
    print(f"Ready: {gpu}", flush=True)
    print(f"Listening on microphone {selected_device}; completed utterances will appear below. Ctrl+C stops.", flush=True)

    speech: list[np.ndarray] = []
    silence_blocks = 0
    speaking = False
    started_at = 0.0

    try:
        with sd.InputStream(
            samplerate=SAMPLE_RATE,
            blocksize=block_size,
            device=input_device,
            channels=1,
            dtype="float32",
            callback=callback,
        ):
            while True:
                chunk = audio_queue.get()
                loud = rms(chunk) >= args.threshold
                if not speaking:
                    if not loud:
                        continue
                    speaking = True
                    started_at = time.monotonic()
                    speech = [chunk]
                    silence_blocks = 0
                    print("…", end="", flush=True)
                    continue

                speech.append(chunk)
                silence_blocks = 0 if loud else silence_blocks + 1
                if silence_blocks < silence_blocks_to_finish and len(speech) < max_blocks:
                    continue

                utterance = np.concatenate(speech)
                print("\r" + " " * 20 + "\r", end="", flush=True)
                text = transcribe(model, utterance)
                if text:
                    print(text, flush=True)
                else:
                    print("[No speech recognized]", flush=True)
                speaking = False
                speech = []
                silence_blocks = 0
    except KeyboardInterrupt:
        print("\nStopped.")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())

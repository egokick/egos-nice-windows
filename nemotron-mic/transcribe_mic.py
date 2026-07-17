"""Live microphone transcription with NVIDIA Nemotron 3.5 ASR Streaming.

Press Ctrl+C to stop. The first result takes a few seconds while the model
collects its left-context audio; later output is emitted per streaming chunk.
"""

from __future__ import annotations

import argparse
import queue
import threading
from collections.abc import Iterator

import numpy as np
import sounddevice as sd
import torch
from transformers import AutoModelForRNNT, AutoProcessor, TextIteratorStreamer

MODEL_ID = "nvidia/nemotron-3.5-asr-streaming-0.6b"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Transcribe a microphone with Nemotron 3.5 ASR Streaming.")
    parser.add_argument("--language", default="en-US", help="Locale prompt, such as en-US, de-DE, or auto.")
    parser.add_argument(
        "--lookahead",
        type=int,
        choices=(0, 1, 3, 6, 13),
        default=0,
        help="Right-context frames: 0=80 ms, 1=160 ms, 3=320 ms, 6=560 ms, 13=1120 ms.",
    )
    parser.add_argument("--device", type=int, default=None, help="Optional sounddevice input-device index.")
    parser.add_argument("--list-devices", action="store_true", help="List audio devices and exit.")
    return parser.parse_args()


class MicrophoneBuffer:
    def __init__(self, sample_rate: int, device: int | None) -> None:
        self.sample_rate = sample_rate
        self.device = device
        self._chunks: queue.Queue[np.ndarray] = queue.Queue()

    def _callback(self, indata: np.ndarray, frames: int, time: object, status: sd.CallbackFlags) -> None:
        if status:
            print(f"\n[audio: {status}]", flush=True)
        self._chunks.put(indata[:, 0].copy())

    def capture(self) -> Iterator[np.ndarray]:
        # 80 ms blocks match the model's smallest streaming frame.
        blocksize = int(self.sample_rate * 0.08)
        with sd.InputStream(
            samplerate=self.sample_rate,
            channels=1,
            dtype="float32",
            blocksize=blocksize,
            device=self.device,
            callback=self._callback,
        ):
            while True:
                yield self._chunks.get()


def create_streaming_inputs(
    processor: AutoProcessor,
    microphone: MicrophoneBuffer,
    language: str,
    device: torch.device,
    dtype: torch.dtype,
) -> tuple[dict[str, torch.Tensor], Iterator[torch.Tensor]]:
    """Prepare the initial prompt-bearing chunk and continuous cache-aware chunks."""
    captured = np.empty(0, dtype=np.float32)
    audio_chunks = microphone.capture()

    def collect_until(required_samples: int) -> None:
        nonlocal captured
        while captured.size < required_samples:
            captured = np.concatenate((captured, next(audio_chunks)))

    collect_until(processor.num_samples_first_audio_chunk)
    first_inputs = processor(
        captured[: processor.num_samples_first_audio_chunk],
        sampling_rate=processor.feature_extractor.sampling_rate,
        is_streaming=True,
        is_first_audio_chunk=True,
        language=language,
        return_tensors="pt",
    ).to(device, dtype=dtype)

    def stream_features() -> Iterator[torch.Tensor]:
        yield first_inputs.input_features[:, : processor.num_mel_frames_first_audio_chunk, :]

        mel_frame_index = processor.num_mel_frames_first_audio_chunk
        hop_length = processor.feature_extractor.hop_length
        n_fft = processor.feature_extractor.n_fft

        while True:
            # At the 80 ms setting, the first right-context window begins
            # slightly before sample zero. Clamp it rather than letting
            # NumPy interpret a negative start as an index from the end.
            start_index = max(0, mel_frame_index * hop_length - n_fft // 2)
            end_index = start_index + processor.num_samples_per_audio_chunk
            collect_until(end_index)
            chunk = processor(
                captured[start_index:end_index],
                sampling_rate=processor.feature_extractor.sampling_rate,
                is_streaming=True,
                is_first_audio_chunk=False,
                language=language,
                return_tensors="pt",
            ).to(device, dtype=dtype)
            yield chunk.input_features
            mel_frame_index += processor.num_mel_frames_per_audio_chunk

    return dict(first_inputs), stream_features()


def main() -> None:
    args = parse_args()
    if args.list_devices:
        print(sd.query_devices())
        return
    if not torch.cuda.is_available():
        raise SystemExit("CUDA was not detected. Install a CUDA-enabled PyTorch build, then run this again.")

    print("Loading Nemotron 3.5 ASR Streaming onto the NVIDIA GPU…", flush=True)
    processor = AutoProcessor.from_pretrained(MODEL_ID)
    processor.set_num_lookahead_tokens(args.lookahead)
    model = AutoModelForRNNT.from_pretrained(MODEL_ID, dtype=torch.float16).to("cuda").eval()
    device = next(model.parameters()).device
    print(f"Ready: {torch.cuda.get_device_name(device)} | {processor.streaming_latency_ms} ms streaming latency")
    print(f"Listening on microphone {args.device if args.device is not None else 'default'}; Ctrl+C stops.\n")

    microphone = MicrophoneBuffer(processor.feature_extractor.sampling_rate, args.device)
    first_inputs, generator = create_streaming_inputs(processor, microphone, args.language, device, model.dtype)
    streamer = TextIteratorStreamer(processor.tokenizer, skip_special_tokens=True, timeout=0.5)
    generation_failures: queue.Queue[BaseException] = queue.Queue(maxsize=1)

    def run_generation() -> None:
        try:
            generation_inputs = {
                **first_inputs,
                "input_features": generator,
                "streamer": streamer,
                "num_lookahead_tokens": args.lookahead,
            }
            model.generate(**generation_inputs)
        except BaseException as error:
            generation_failures.put(error)

    generation = threading.Thread(
        target=run_generation,
        daemon=True,
    )
    generation.start()

    try:
        while generation.is_alive():
            try:
                print(next(streamer), end="", flush=True)
            except queue.Empty:
                if not generation_failures.empty():
                    raise generation_failures.get()
        if not generation_failures.empty():
            raise generation_failures.get()
    except KeyboardInterrupt:
        print("\nStopped.")


if __name__ == "__main__":
    main()

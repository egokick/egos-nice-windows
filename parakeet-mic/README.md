# Parakeet microphone console

This is a local GPU console for `nvidia/parakeet-tdt-0.6b-v2`. It records 16 kHz mono audio, detects an utterance using a simple volume threshold, and prints the completed transcript with punctuation and casing.

Run it with `start-parakeet-mic.bat`. Press `Ctrl+C` to stop.

Useful options:

```
start-parakeet-mic.bat --list-devices
start-parakeet-mic.bat --device 1
start-parakeet-mic.bat --threshold 0.004
start-parakeet-mic.bat --device 1 --test-microphone
```

This Parakeet v2 release is an utterance/chunk transcription model, rather than a cache-aware streaming model like Nemotron. A line appears after roughly 700 ms of silence (or after the 15 second maximum). For the lowest latency, stop any loaded Ollama model first: `ollama stop mars-you`.

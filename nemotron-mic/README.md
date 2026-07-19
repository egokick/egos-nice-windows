# Nemotron microphone console

This is a local, GPU-accelerated console microphone transcriber using `nvidia/nemotron-3.5-asr-streaming-0.6b`.

Run `start.bat`. It installs Python 3.12 and the app-local virtual environment when needed. The first launch then downloads NVIDIA's model and shows incremental text from the default microphone. Press `Ctrl+C` to stop.

The app uses about 1.2 GB of VRAM. Close or unload a large Ollama chat model first for the most GPU headroom.

Options:

```powershell
.\.venv\Scripts\python.exe .\transcribe_mic.py --list-devices
.\.venv\Scripts\python.exe .\transcribe_mic.py --device 3 --language en-US
.\.venv\Scripts\python.exe .\transcribe_mic.py --language auto --lookahead 6
```

`--lookahead 6` is the 560 ms streaming setting. Choose `3` for 320 ms or `13` for 1.12 seconds and slightly more context.

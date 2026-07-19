# Coder Files agent

`coder-files` is the installed Qwen3-Coder model wrapped in a small, local, read-only tool host. The model can list project files, search text, and read text files; it cannot edit, delete, run commands, access the network, or escape the folder root you choose.

Run against the default workspace, `C:\source\egos-nice-windows`. The launcher installs Python 3.12, Ollama, and the `coder-files` model when they are missing:

```bat
start.bat
```

Run against another folder you choose:

```bat
start.bat --root "C:\source\another-project"
```

Use `exit` to quit. The Ollama desktop app/server must be running. All tool calls and their target paths are printed before the file is read.

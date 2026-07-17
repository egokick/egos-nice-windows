# Coder Files agent

`coder-files` is the installed Qwen3-Coder model wrapped in a small, local, read-only tool host. The model can list project files, search text, and read text files; it cannot edit, delete, run commands, access the network, or escape the folder root you choose.

Run against the default workspace, `C:\source\egos-nice-windows`:

```bat
start-coder-files.bat
```

Run against another folder you choose:

```bat
start-coder-files.bat --root "C:\source\another-project"
```

Use `exit` to quit. The Ollama desktop app/server must be running. All tool calls and their target paths are printed before the file is read.

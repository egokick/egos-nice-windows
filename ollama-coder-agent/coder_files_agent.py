"""Read-only local coding agent powered by Ollama tool calling."""

from __future__ import annotations

import argparse
import json
import os
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


DEFAULT_ROOT = Path(r"C:\source\egos-nice-windows")
MODEL = "coder-files"
MAX_FILE_BYTES = 512_000


TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "list_files",
            "description": "List files and folders beneath a relative directory inside the configured project root.",
            "parameters": {
                "type": "object",
                "properties": {
                    "path": {"type": "string", "description": "Relative directory path; defaults to the project root."},
                    "max_results": {"type": "integer", "description": "Maximum entries to return, 1-500; defaults to 100."},
                },
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "read_file",
            "description": "Read a UTF-8-compatible text file inside the configured project root. Use line ranges for large files.",
            "parameters": {
                "type": "object",
                "required": ["path"],
                "properties": {
                    "path": {"type": "string", "description": "Relative file path."},
                    "start_line": {"type": "integer", "description": "First line to return, 1-based; defaults to 1."},
                    "end_line": {"type": "integer", "description": "Last line to return, inclusive; defaults to 400 lines after start."},
                },
            },
        },
    },
    {
        "type": "function",
        "function": {
            "name": "search_files",
            "description": "Search text content for a literal query inside the configured project root.",
            "parameters": {
                "type": "object",
                "required": ["query"],
                "properties": {
                    "query": {"type": "string", "description": "Literal case-insensitive text to find."},
                    "path": {"type": "string", "description": "Relative starting directory; defaults to the project root."},
                    "max_results": {"type": "integer", "description": "Maximum matching lines to return, 1-200; defaults to 50."},
                },
            },
        },
    },
]


def bounded_path(root: Path, relative: str = ".") -> Path:
    candidate = (root / relative).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as exc:
        raise ValueError("Path escapes the configured project root.") from exc
    return candidate


def limit(value: Any, default: int, minimum: int, maximum: int) -> int:
    try:
        return max(minimum, min(maximum, int(value)))
    except (TypeError, ValueError):
        return default


def list_files(root: Path, path: str = ".", max_results: int = 100) -> str:
    folder = bounded_path(root, path)
    if not folder.is_dir():
        return json.dumps({"error": f"Not a directory: {path}"})
    maximum = limit(max_results, 100, 1, 500)
    entries: list[str] = []
    for child in sorted(folder.iterdir(), key=lambda item: (not item.is_dir(), item.name.lower())):
        suffix = "/" if child.is_dir() else ""
        entries.append(str(child.relative_to(root)).replace("\\", "/") + suffix)
        if len(entries) >= maximum:
            break
    return json.dumps({"path": str(folder.relative_to(root)) or ".", "entries": entries, "truncated": len(entries) >= maximum})


def read_file(root: Path, path: str, start_line: int = 1, end_line: int | None = None) -> str:
    file_path = bounded_path(root, path)
    if not file_path.is_file():
        return json.dumps({"error": f"Not a file: {path}"})
    if file_path.stat().st_size > MAX_FILE_BYTES:
        return json.dumps({"error": f"File exceeds {MAX_FILE_BYTES} bytes; use a smaller source file or inspect it manually."})
    try:
        content = file_path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError as exc:
        return json.dumps({"error": str(exc)})
    first = max(1, int(start_line))
    last = limit(end_line, first + 399, first, first + 1999)
    selected = content[first - 1 : last]
    numbered = "\n".join(f"{number}: {line}" for number, line in enumerate(selected, start=first))
    return json.dumps({"path": str(file_path.relative_to(root)).replace("\\", "/"), "lines": numbered, "total_lines": len(content)})


def likely_text(file_path: Path) -> bool:
    try:
        return b"\0" not in file_path.read_bytes()[:4096]
    except OSError:
        return False


def search_files(root: Path, query: str, path: str = ".", max_results: int = 50) -> str:
    folder = bounded_path(root, path)
    if not folder.is_dir():
        return json.dumps({"error": f"Not a directory: {path}"})
    maximum = limit(max_results, 50, 1, 200)
    needle = query.casefold()
    matches: list[dict[str, Any]] = []
    ignored = {".git", ".venv", "node_modules", "__pycache__"}
    for directory, folders, files in os.walk(folder):
        folders[:] = [name for name in folders if name not in ignored]
        for name in files:
            candidate = Path(directory) / name
            if candidate.stat().st_size > MAX_FILE_BYTES or not likely_text(candidate):
                continue
            try:
                for number, line in enumerate(candidate.read_text(encoding="utf-8", errors="replace").splitlines(), start=1):
                    if needle in line.casefold():
                        matches.append({"path": str(candidate.relative_to(root)).replace("\\", "/"), "line": number, "text": line[:500]})
                        if len(matches) >= maximum:
                            return json.dumps({"matches": matches, "truncated": True})
            except OSError:
                continue
    return json.dumps({"matches": matches, "truncated": False})


def call_ollama(messages: list[dict[str, Any]]) -> dict[str, Any]:
    payload = json.dumps({"model": MODEL, "messages": messages, "tools": TOOLS, "stream": False, "think": False}).encode()
    request = urllib.request.Request("http://127.0.0.1:11434/api/chat", data=payload, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=600) as response:
        return json.loads(response.read())


def execute_tool(root: Path, call: dict[str, Any]) -> tuple[str, str]:
    function = call.get("function", {})
    name = function.get("name", "")
    arguments = function.get("arguments", {}) or {}
    print(f"\n[tool] {name}({json.dumps(arguments)})")
    try:
        if name == "list_files":
            return name, list_files(root, **arguments)
        if name == "read_file":
            return name, read_file(root, **arguments)
        if name == "search_files":
            return name, search_files(root, **arguments)
        return name, json.dumps({"error": f"Unknown tool: {name}"})
    except (TypeError, ValueError) as exc:
        return name, json.dumps({"error": str(exc)})


def run_turn(root: Path, messages: list[dict[str, Any]], prompt: str) -> None:
    messages.append({"role": "user", "content": prompt})
    for _ in range(12):
        try:
            response = call_ollama(messages)
        except urllib.error.URLError as exc:
            print(f"Ollama is unavailable: {exc.reason}")
            return
        message = response["message"]
        messages.append(message)
        calls = message.get("tool_calls") or []
        if not calls:
            print("\n" + (message.get("content") or "[No response text.]"))
            return
        for tool_call in calls:
            name, result = execute_tool(root, tool_call)
            messages.append({"role": "tool", "tool_name": name, "content": result})
    print("\nStopped after 12 tool rounds to prevent an accidental loop.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=str(DEFAULT_ROOT), help="Folder the agent may read (default: this workspace).")
    args = parser.parse_args()
    root = Path(args.root).expanduser().resolve()
    if not root.is_dir():
        parser.error(f"--root is not a directory: {root}")
    print(f"Coder Files — read-only root: {root}")
    print("Ask about the project. Type 'exit' to quit or 'clear' to discard chat history.")
    messages: list[dict[str, Any]] = []
    while True:
        try:
            prompt = input("\nyou> ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\nBye.")
            return 0
        if prompt.lower() in {"exit", "quit"}:
            return 0
        if prompt.lower() == "clear":
            messages.clear()
            print("Chat history cleared.")
            continue
        if prompt:
            run_turn(root, messages, prompt)


if __name__ == "__main__":
    raise SystemExit(main())

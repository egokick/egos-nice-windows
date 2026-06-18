# VoiceCodex

VoiceCodex is a Windows tray utility that toggles local Whisper microphone transcription with a left click.
When enabled, transcribed speech is gated for intent. Casual/test speech is ignored, VoiceCodex/help questions are answered locally, common desktop commands run locally, flexible wording is mapped semantically to known local capabilities, and unknown action commands are handed to one persistent `VoiceCodex Controller` Codex terminal.

## Build

```powershell
dotnet build .\voicecodex\voicecodex.csproj
```

## Run

```powershell
dotnet run --project .\voicecodex\voicecodex.csproj
```

Left-click the tray icon to enable or disable listening. Right-click for the menu.

The tray menu shows live feedback:

- `Status` changes between disabled, listening, speech detected, hearing speech, and launching Codex.
- `Heard` shows recording/transcription status and the latest local Whisper transcript.
- `Last accepted` shows the last phrase sent to Codex.
- `Last dispatch` shows whether a command was ignored or sent to the controller.
- `Start controller Codex` starts the persistent controller session.
- `Show activity log` opens a live window with recognition and launch events.
- `Test feedback` verifies that tray notifications and the activity log are working.
- `Self-test: switch apps via Codex` dispatches a spoken-style Alt+Tab request.
- `Self-test: switch terminal tab via Codex` dispatches a spoken-style Windows Terminal Ctrl+Tab request.
- `Self-test: multiple asks via Codex` dispatches three asks from one running tray-app session.
- `Voice responses` toggles spoken status/transcript/Codex-result feedback.
- `Whisper tiny.en`, `base.en`, and `small.en` choose the local model. `tiny.en` is the default because VoiceCodex is optimized for short control commands; `base.en` and `small.en` are more accurate but slower.

The realtime route is:

```text
targeted existing terminal handoff -> deterministic local command -> semantic local command -> persistent controller handoff -> slow classifier fallback
```

Targeted handoff is for sending a prompt into an already-running Codex terminal that already has project context. VoiceCodex extracts the target hint and payload, finds likely terminal windows by fuzzy title matching, focuses the target, and pastes only the payload. If more than one terminal is plausible, it asks a short clarification and keeps the pending payload for the next answer. Clarification answers can be `first`, `second`, `newest`, `active terminal`, `left`, `right`, or `cancel`.

Fast unknown action commands are pasted into the persistent controller, which runs:

```text
codex -m gpt-5.5 -c model_reasoning_effort="low" -c service_tier="fast" -s danger-full-access -a never
```

Only the slow classifier fallback launches a one-shot read-only classifier:

```text
codex -m gpt-5.5 -c model_reasoning_effort="low" -c service_tier="fast" -s read-only -a never exec --skip-git-repo-check -C %USERPROFILE% -o <temp.json> -
```

Local Whisper uses `Whisper.net` with the CPU runtime. On first listening start, it downloads the selected model once into:

```text
%LOCALAPPDATA%\VoiceCodex\models
```

Recorded utterance snippets are temporary files under `%LOCALAPPDATA%\VoiceCodex\utterances` and are deleted after transcription.

For dispatch testing without using the microphone:

```powershell
dotnet run --project .\voicecodex\voicecodex.csproj -- --dispatch "switch to the next tab in the existing Windows Terminal window and then stop"
```

For gate-only testing:

```powershell
dotnet run --project .\voicecodex\voicecodex.csproj -- --gate-many "this is a test one two ;; codex switch to the next terminal tab"
```

There is no required wake phrase. Say clear commands such as:

```text
switch to the next terminal tab
switch windows
make this window bigger
go back to the terminal
next shell tab
put git status here
switch to the Codex terminal handling the Rust game and tell it to design X with these details
tell the Rust game Codex terminal to review the combat loop
ask the right Codex terminal to update the player movement
focus terminal
bring the terminal to the front and maximize it
maximize the active window
type hello into the terminal
type hello into the active app
click center
press control tab
```

For ambiguous commands, optionally start with `Codex`, for example `Codex type this into the terminal`.

For multiple dispatch tests:

```powershell
dotnet run --project .\voicecodex\voicecodex.csproj -- --dispatch-many "this is a test one two ;; codex say first test done and then stop ;; codex say second test done and then stop"
```

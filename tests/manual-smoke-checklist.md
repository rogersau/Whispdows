# Whispdows manual smoke checklist

Use a non-elevated Windows 11 x64 session and the packaged installer.

## Install and lifecycle

- Install without a UAC elevation prompt.
- Install each setup type once: **Transcribe only**, **Meeting Notes only**, and **Transcribe and Meeting Notes**.
- Confirm the selected type installs only its required model: `ggml-small.en.bin`, `ggml-medium.en.bin`, or both.
- Confirm `Whispdows.exe`, the .NET runtime, and `runtimes\win-x64\whisper.dll` are under `%LOCALAPPDATA%\Programs\Whispdows`.
- With Ollama absent, confirm the installer shows an unchecked **Install Ollama for local AI cleanup** task. Leave it unchecked and confirm no Ollama files or model downloads are created.
- Repeat with the task selected and an internet connection. Confirm Windows Package Manager installs Ollama for the current user, the Whispdows install completes, and no model is pulled.
- Run the Whispdows installer again with Ollama present. Confirm the Ollama task is not shown and the existing installation is unchanged.
- Temporarily test without Windows Package Manager available. Confirm selecting the Ollama task produces a clear explanation and offers the official Ollama Windows page without blocking Whispdows installation.
- Confirm `Whispdows.exe`, the .NET runtime, `runtimes\win-x64\whisper.dll`, and `models\ggml-small.en.bin` are under `%LOCALAPPDATA%\Programs\Whispdows`.
- Confirm an existing `%LOCALAPPDATA%\Whispdows\settings.json` and `secrets.dat` survive an upgrade; a legacy `.env` is migrated and cleared on first launch.
- Switch from **Both** to each single-feature setup and confirm the unselected model and tray action are removed.
- Confirm the optional launch-at-login task creates a per-user startup entry.
- Exit from the tray and confirm the pill closes, microphone capture ends, and the global shortcut is released.
- Uninstall once preserving user data, then again choosing explicit user-data removal.
- Confirm uninstalling Whispdows does not uninstall Ollama.

## Dictation targets

For each non-elevated target below, place ordinary text on the clipboard, focus an editable field, hold `RightCtrl`, dictate a short sentence, and release:

- Notepad
- Edge or Chrome
- Outlook
- Teams
- VS Code

Confirm the sentence is pasted once and the original clipboard text is restored.

## Meeting notes

- Grant desktop microphone access, play audio through the default output device, record a short meeting, and confirm both voices and system audio are audible in the saved WAV.
- Confirm stopping a meeting creates matching `~/MeetingNotes/YYYY-MM-DD-HHMM.md` and `.wav` files with five summary bullets, decisions, action items, a divider, and the full transcript.
- Record twice in one minute and confirm the second files use `-02` without overwriting the first.
- With cloud keys absent and Ollama running, confirm local `medium.en` transcription and local note generation complete with the network disconnected.
- Force transcription and note-generation failures separately and confirm the recovery Markdown/WAV remain local.

## Safety paths

- Change focus while transcription is running. Confirm the result remains on the clipboard and the pill says `Copied — target changed`.
- Confirm the pill expands for `Copied — target changed`, truncates an unusually long error cleanly, and remains centred on the target monitor.
- Whispdows toward an elevated target. Confirm automatic paste is blocked and the result remains on the clipboard for manual paste.
- Deny microphone access. Confirm `Microphone unavailable` and no paste.
- Disconnect networking in local/basic mode. Confirm dictation still completes.
- Configure a cloud provider with an invalid key and a local/basic fallback. Confirm one failed cloud request is followed by fallback, with no repeated request.
- Configure Azure Speech with the resource's matching region and locale. Confirm one dictation is transcribed, then use a deliberately invalid key and confirm local fallback runs without retrying.

## Settings and local AI cleanup

- Double-click the tray icon and confirm Settings opens.
- Navigate every Settings field using only `Tab` and `Shift+Tab`; confirm focused text fields, selectors, and buttons have a visible focus cue.
- Select **Local AI model (Ollama)**. Confirm only the local model panel appears, the endpoint defaults to `http://127.0.0.1:11434/v1`, and each Gemma/Qwen preset fills the editable model field.
- Pull `gemma3:1b`, run one local cleanup, and confirm the tray tooltip/status progresses through listening, transcribing, and cleaning.
- Stop Ollama and repeat with **Fall back to basic cleanup** enabled. Confirm the transcript is preserved and pasted after one failed local request.
- Enter a non-loopback endpoint such as `https://example.com/v1`. Confirm Save & Apply rejects it and focuses the endpoint field.
- Check Settings and the recording pill at 100%, 150%, and 200% display scaling on both a primary and secondary monitor.

## Privacy inspection

Inspect `%LOCALAPPDATA%\Whispdows\logs` after local and cloud runs:

- State, duration, provider, and exception-type metadata may appear.
- Audio, transcript text, clipboard text, API keys, request bodies, and authorization headers must not appear.
- While using Ollama cleanup, confirm requests go only to the configured loopback endpoint and contain no authorization header.
- No more than five log files should remain.

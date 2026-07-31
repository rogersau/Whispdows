# Dictate manual smoke checklist

Use a non-elevated Windows 11 x64 session and the packaged installer.

## Install and lifecycle

- Install without a UAC elevation prompt.
- Confirm `Dictate.exe`, the .NET runtime, `runtimes\win-x64\whisper.dll`, and `models\ggml-small.en.bin` are under `%LOCALAPPDATA%\Programs\Dictate`.
- Confirm an existing `%LOCALAPPDATA%\Dictate\settings.json` and `.env` survive an upgrade.
- Confirm the optional launch-at-login task creates a per-user startup entry.
- Exit from the tray and confirm the pill closes, microphone capture ends, and the global shortcut is released.
- Uninstall once preserving user data, then again choosing explicit user-data removal.

## Dictation targets

For each non-elevated target below, place ordinary text on the clipboard, focus an editable field, hold `RightCtrl`, dictate a short sentence, and release:

- Notepad
- Edge or Chrome
- Outlook
- Teams
- VS Code

Confirm the sentence is pasted once and the original clipboard text is restored.

## Safety paths

- Change focus while transcription is running. Confirm the result remains on the clipboard and the pill says `Copied — target changed`.
- Dictate toward an elevated target. Confirm automatic paste is blocked and the result remains on the clipboard for manual paste.
- Deny microphone access. Confirm `Microphone unavailable` and no paste.
- Disconnect networking in local/basic mode. Confirm dictation still completes.
- Configure a cloud provider with an invalid key and a local/basic fallback. Confirm one failed cloud request is followed by fallback, with no repeated request.

## Privacy inspection

Inspect `%LOCALAPPDATA%\Dictate\logs` after local and cloud runs:

- State, duration, provider, and exception-type metadata may appear.
- Audio, transcript text, clipboard text, API keys, request bodies, and authorization headers must not appear.
- No more than five log files should remain.

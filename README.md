# Dictate

Dictate is a small Windows tray application for hold-to-talk AI dictation. All five implementation slices are present: the Windows tray shell, global hold shortcut, WASAPI microphone capture, local or cloud transcription, deterministic or cloud cleanup, focus-safe clipboard paste, rolling privacy-safe logs, and a per-user installer.

The local path works without network access once the model is present. OpenAI and Groq are optional and are never selected automatically.

## Build and run

The project targets Windows 11 x64 and .NET 10:

```powershell
.\scripts\Get-WhisperModel.ps1
dotnet build .\Dictate.sln
dotnet run --project .\src\Dictate\Dictate.csproj
```

The model command downloads the official whisper.cpp `small.en` model, verifies its pinned hash, and places it under `models\`. Dictate never downloads a model at runtime.

To build the complete self-contained release and installer, install the .NET 10 SDK and Inno Setup, then run:

```powershell
.\scripts\Build-Release.ps1 -Version 0.1.0
```

The script produces a win-x64 folder under `artifacts\publish\win-x64` and `artifacts\installer\Dictate-Setup.exe`. The installer contains the .NET runtime, whisper.cpp native runtime, and verified `small.en` model; the installed app does not require .NET, Python, Docker, Node.js, or a separate model download.

The application starts without a normal window. Find it in the notification area. Its menu can enable or disable dictation, toggle launch at login, reload settings, open the settings folder, and exit.

When dictation is enabled, hold the configured shortcut—`RightCtrl` by default—to record. Release it to stop, transcribe locally, clean the text, and paste into the window that was active when recording began. Press `Escape` to cancel. Repeated presses are ignored while a recording is being processed, recordings shorter than 250 ms are discarded, and capture stops at `audio.maxSeconds`.

If focus changes during processing, Dictate leaves the result on the clipboard and shows `Copied — target changed`. Clipboard contents are restored only when no other application changed them after Dictate wrote its text.

## Settings

The application creates this file on first start:

```text
%LOCALAPPDATA%\Dictate\settings.json
```

The checked-in [settings.example.json](settings.example.json) shows the complete configuration shape. Settings are validated before they are loaded or saved; an invalid reload leaves the last valid in-memory settings active.

The application also creates a plain-text secrets file:

```text
%LOCALAPPDATA%\Dictate\.env
```

Set only the keys for providers you select:

```dotenv
OPENAI_API_KEY=
GROQ_API_KEY=
```

Select `openai` or `groq` under `transcription.provider` for cloud transcription. OpenAI defaults to `gpt-4o-transcribe`; Groq defaults to `whisper-large-v3-turbo`. A missing key is reported before recording unless `fallbackToLocal` is enabled, in which case Dictate skips the cloud request and uses the validated local model. Failed and timed-out requests are not retried.

Cloud cleanup is independent of transcription. Select `openai` or `groq` under `cleanup.provider` and set `cleanup.model` to a chat-completions model available to that provider account. When `fallbackToBasic` is enabled, a missing key, timeout, API error, or malformed response falls back to deterministic basic cleanup so a successful transcript is not discarded.

Supported shortcut examples include:

```text
RightCtrl
Ctrl+Win+Space
F13
```

The trigger can also be a letter, digit, or `F1`–`F24`. `Escape` is reserved for cancelling an active recording.

The launch-at-login option uses the per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key and does not require administrator access.

## Install and uninstall

`Dictate-Setup.exe` installs without elevation to:

```text
%LOCALAPPDATA%\Programs\Dictate
```

Settings, API keys, and logs live separately under `%LOCALAPPDATA%\Dictate`. Upgrades preserve these files. Uninstall also preserves them by default and explicitly asks whether to remove them.

The installer can enable launch at login and creates Start menu shortcuts for Dictate, this README, and Uninstall. It installs no service, driver, scheduled task, shell extension, or browser extension.

## Windows permissions and limitations

Windows has no Accessibility or Input Monitoring permission switch for an ordinary desktop application like Dictate.

For microphone access on Windows 11:

1. Open **Settings**.
2. Go to **Privacy & security → Microphone**.
3. Turn on **Microphone access**.
4. Turn on **Let desktop apps access your microphone**.

Windows normally shows a microphone indicator in the notification area while capture is active.

For keyboard and paste access, Dictate uses a global low-level keyboard hook to recognize only the configured shortcut and `SendInput` to press `Ctrl+V`. There is no separate permission to grant.

The physical `Fn` key is commonly handled by keyboard firmware and may not be visible to Windows. The default shortcut is `RightCtrl`; a hardware button can be remapped to `F13` when needed.

Dictate will remain a non-elevated application. Windows does not allow a normal process to inject input into a target running as administrator; the completed dictation will remain on the clipboard in that case.

The launch-at-login toggle can also be inspected or disabled under **Settings → Apps → Startup** or Task Manager’s Startup Apps page.

An unsigned personal installer may trigger Microsoft Defender SmartScreen. Build from source or code-sign the release where practical. Do not disable Defender, SmartScreen, or antivirus protection globally to install Dictate.

## Privacy

Audio capture and transcripts stay in memory and are explicitly released after processing or cancellation. With `transcription.provider` set to `local` and `cleanup.provider` set to `basic` or `none`, no audio or transcript is sent over the network. Cloud transcription sends the completed WAV recording to the selected provider. Cloud cleanup sends only the raw transcript and fixed cleanup instructions to the selected provider; it does not send surrounding app or clipboard context.

API keys remain in the user-owned plain-text `.env` file. Dictate has no remote logging, analytics, telemetry, crash reporting, or transcript history.

Local logs are kept under `%LOCALAPPDATA%\Dictate\logs`. At most five 256 KiB files contain state changes, operation durations, provider names, and exception types. They never include audio, transcript text, clipboard contents, request bodies, API keys, or authorization headers.

The repeatable manual release checks are in [tests/manual-smoke-checklist.md](tests/manual-smoke-checklist.md).

# Dictate

Dictate is a small Windows tray application for hold-to-talk AI dictation. The repository currently contains Slices 1 through 3: the Windows tray shell, global hold shortcut, WASAPI microphone capture, persistent local whisper.cpp transcription, deterministic cleanup, and focus-safe clipboard paste.

Cloud transcription and LLM cleanup are intentionally deferred to Slice 4. The local path works without network access once the model is present.

## Build and run

The project targets Windows 11 x64 and .NET 10:

```powershell
.\scripts\Get-WhisperModel.ps1
dotnet build .\Dictate.sln
dotnet run --project .\src\Dictate\Dictate.csproj
```

The model command downloads the official whisper.cpp `small.en` model, verifies its pinned hash, and places it under `models\`. Dictate never downloads a model at runtime.

The application starts without a normal window. Find it in the notification area. Its menu can enable or disable dictation, toggle launch at login, reload settings, open the settings folder, and exit.

When dictation is enabled, hold the configured shortcut—`RightCtrl` by default—to record. Release it to stop, transcribe locally, clean the text, and paste into the window that was active when recording began. Press `Escape` to cancel. Repeated presses are ignored while a recording is being processed, recordings shorter than 250 ms are discarded, and capture stops at `audio.maxSeconds`.

If focus changes during processing, Dictate leaves the result on the clipboard and shows `Copied — target changed`. Clipboard contents are restored only when no other application changed them after Dictate wrote its text.

## Settings

The application creates this file on first start:

```text
%LOCALAPPDATA%\Dictate\settings.json
```

The checked-in [settings.example.json](settings.example.json) shows the complete configuration shape. Settings are validated before they are loaded or saved; an invalid reload leaves the last valid in-memory settings active.

Supported shortcut examples include:

```text
RightCtrl
Ctrl+Win+Space
F13
```

The trigger can also be a letter, digit, or `F1`–`F24`. `Escape` is reserved for cancelling an active recording.

The launch-at-login option uses the per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key and does not require administrator access.

## Windows permissions and limitations

Windows microphone access is managed under **Settings → Privacy & security → Microphone**. Turn on **Microphone access** and **Let desktop apps access your microphone**.

The physical `Fn` key is commonly handled by keyboard firmware and may not be visible to Windows. The planned default shortcut is `RightCtrl`; a hardware button can be remapped to `F13` when needed.

Dictate will remain a non-elevated application. Windows does not allow a normal process to inject input into a target running as administrator; the completed dictation will remain on the clipboard in that case.

## Privacy

Audio capture and transcripts stay in memory and are explicitly released after processing or cancellation. With `transcription.provider` set to `local` and `cleanup.provider` set to `basic` or `none`, no audio or transcript is sent over the network. Cloud transcription or cleanup, when configured in Slice 4, will send the relevant audio or transcript to the selected provider. No telemetry or transcript history is planned.

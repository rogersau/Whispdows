# Dictate

Dictate is a small Windows tray application for hold-to-talk AI dictation. The repository currently contains Slice 1: the WPF shell, tray menu, local settings file, per-user launch-at-login toggle, and non-activating recording pill.

Keyboard capture, microphone recording, transcription, cleanup, and paste are intentionally deferred to later slices.

## Build and run

The project targets Windows 11 x64 and .NET 10:

```powershell
dotnet build .\Dictate.sln
dotnet run --project .\src\Dictate\Dictate.csproj
```

The application starts without a normal window. Find it in the notification area. Its menu can enable or disable dictation, toggle launch at login, reload settings, open the settings folder, and exit.

## Settings

The application creates this file on first start:

```text
%LOCALAPPDATA%\Dictate\settings.json
```

The checked-in [settings.example.json](settings.example.json) shows the complete configuration shape. Settings are validated before they are loaded or saved; an invalid reload leaves the last valid in-memory settings active.

The launch-at-login option uses the per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key and does not require administrator access.

## Windows permissions and limitations

Later slices will use the microphone and a low-level keyboard hook. Windows microphone access is managed under **Settings → Privacy & security → Microphone**. Turn on **Microphone access** and **Let desktop apps access your microphone**.

The physical `Fn` key is commonly handled by keyboard firmware and may not be visible to Windows. The planned default shortcut is `RightCtrl`; a hardware button can be remapped to `F13` when needed.

Dictate will remain a non-elevated application. Windows does not allow a normal process to inject input into a target running as administrator; the completed dictation will remain on the clipboard in that case.

## Privacy

The final application is designed to keep local audio and transcripts in memory only. Local transcription will not need network access. Cloud transcription or cleanup, when configured in a later slice, will send the relevant audio or transcript to the selected provider. No telemetry or transcript history is planned.

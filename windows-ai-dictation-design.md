# Dictate: Lean Windows AI Dictation Tool Design

**Status:** Proposed design  
**Target:** Windows 11 x64, one user, per-user installation  
**Working name:** `Dictate`

## 1. Design summary

Build this as one small Windows tray application. It is not a service, does not need a database, does not need an account, and does not run a local web server.

Recommended implementation:

- **C# on .NET 10 LTS**
- **WPF** for the recording pill and minimal Windows UI
- **`System.Windows.Forms.NotifyIcon`** for the Windows notification-area menu
- **NAudio/WASAPI** for microphone capture
- **Whisper.net**, backed by **whisper.cpp**, for persistent in-process local transcription
- Direct `HttpClient` calls for the OpenAI-compatible Groq/OpenAI transcription endpoints
- A low-level Windows keyboard hook for hold-to-talk key-down and key-up events
- Clipboard replacement plus simulated `Ctrl+V` for insertion
- A self-contained, per-user **Inno Setup** installer

The default build should include `small.en` so it works immediately without internet access. The model is loaded lazily on the first local dictation and then kept in memory until the app exits.

### Important simplification: offline cleanup

A local LLM is deliberately **not** included in version 1. Bundling llama.cpp and another model would make the installer, memory use, startup, configuration, and support burden substantially larger.

The tool still works fully offline as follows:

1. Local whisper.cpp transcribes the audio.
2. A small deterministic cleaner trims whitespace, removes obvious repeated filler tokens, and normalises basic punctuation.
3. The result is pasted.

When an OpenAI or Groq key is configured, the raw transcript can instead pass through a cloud LLM for better cleanup. This gives a clear choice:

- **Fully offline:** local transcription + basic cleanup
- **Polished cloud mode:** local or cloud transcription + LLM cleanup

A local LLM cleaner can be added later only if the basic offline cleanup proves inadequate.

## 2. Goals

The application must:

- Run quietly in the background.
- Start recording when a configured shortcut is pressed and held.
- Stop recording when the trigger key is released.
- Show a small, non-activating floating pill while recording.
- Transcribe using either:
  - bundled whisper.cpp locally; or
  - OpenAI/Groq, selected explicitly in configuration.
- Optionally clean the transcript with an LLM.
- Paste into the application that was active when dictation began.
- Restore the previous clipboard after pasting where possible.
- Offer an on/off toggle and launch-at-login toggle in the Windows notification area.
- Store settings locally in readable files.
- Send no telemetry and retain no audio or transcript history.
- Be installable with one standalone setup executable.

## 3. Non-goals

Do not build these in version 1:

- Accounts, sign-in, sync, teams, or multi-user support
- A Windows service
- A browser extension
- A database
- A web dashboard or local web server
- Continuous or streaming live transcription
- Transcript history
- Audio history
- Automatic updates
- A full graphical settings application
- App-specific integrations for Teams, Outlook, browsers, or editors
- Reading surrounding text from the focused application
- Code-specific dictation commands
- Spoken command grammars such as “select previous paragraph”
- GPU/CUDA setup
- macOS or Linux support
- A bundled local LLM

## 4. User experience

### Normal flow

1. `Dictate` starts in the Windows notification area.
2. The user places the cursor in any normal text field.
3. The user holds the configured shortcut, initially `RightCtrl`.
4. A pill appears near the bottom centre of the active monitor:

   `● Listening…`

5. The user speaks.
6. The user releases the trigger key.
7. The pill changes to:

   `◌ Transcribing…`

8. The audio is transcribed and optionally cleaned.
9. The final text is pasted into the original target application.
10. The previous clipboard is restored and the pill disappears.

### Focus safety

The foreground window handle is captured when recording starts. The pill never takes focus.

Before insertion, the app checks that the same target window is still active. If focus changed while transcription was running, it does **not** force focus back or paste into the newly selected application. It places the result on the clipboard and shows:

`Copied — target changed`

This avoids text unexpectedly appearing in the wrong window.

### Cancel and busy behaviour

- Pressing `Escape` while recording cancels the current recording.
- A new hotkey press is ignored while an existing dictation is being processed.
- Recordings shorter than about 250 ms are discarded.
- Recording stops automatically at the configured maximum, initially 90 seconds.

## 5. Windows hotkey decision

### Do not promise direct `Fn` support

On most Windows laptops, the physical `Fn` key is handled by keyboard firmware and does not reach Windows as a normal virtual key or scan code. Therefore, the app cannot reliably offer raw `Fn` as a universal hotkey.

Use a configurable normal Windows key or chord instead. Recommended options are:

- `RightCtrl` — simplest default
- `Ctrl+Win+Space`
- `F13` — useful when a mouse, keyboard, Stream Deck, AutoHotkey, or OEM utility can map a physical button to it

If the user’s keyboard utility can remap `Fn` to `F13`, `F13` can then be configured in `settings.json`.

### Implementation

Use `SetWindowsHookEx` with `WH_KEYBOARD_LL`, not `RegisterHotKey`.

`RegisterHotKey` is appropriate for one-shot activation but does not provide the complete key-down/key-up lifecycle needed for hold-to-talk. The low-level hook should:

- Track only the configured modifiers and trigger key.
- Start recording on the first non-repeat trigger-key down.
- Stop recording on trigger-key up.
- Suppress the configured shortcut while it is being used for dictation so it does not leak into the target application.
- Immediately dispatch work to the WPF dispatcher and return from the hook callback.
- Never record or log unrelated keys.

For a chord such as `Ctrl+Win+Space`, recording begins when `Space` goes down while the required modifiers are held and ends when `Space` is released.

## 6. Architecture

```mermaid
flowchart LR
    Hook[Global keyboard hook] --> Controller[DictationController]
    Tray[Tray menu] --> Controller
    Controller --> Pill[Floating pill]
    Controller --> Recorder[AudioRecorder]
    Recorder --> Transcriber[ITranscriber]
    Transcriber --> Local[WhisperCppTranscriber]
    Transcriber --> Cloud[OpenAI-compatible cloud transcriber]
    Controller --> Cleaner[ITextCleaner]
    Cleaner --> Basic[Basic local cleaner]
    Cleaner --> LLM[OpenAI/Groq LLM cleaner]
    Controller --> Inserter[TextInserter]
    Inserter --> Clipboard[Clipboard snapshot/set/restore]
    Inserter --> SendInput[Simulated Ctrl+V]
    Settings[settings.json + .env] --> Controller
```

### Single orchestration class

`DictationController` owns the workflow and state. Avoid an event bus, dependency-injection framework, background worker service, mediator library, or message broker.

It coordinates these small components:

- `HotkeyHook`
- `AudioRecorder`
- `ITranscriber`
- `ITextCleaner`
- `TextInserter`
- `PillWindow`
- `TrayMenu`
- `SettingsLoader`

Use constructor injection manually in `App.xaml.cs`. Two interfaces are enough: `ITranscriber` and `ITextCleaner`.

## 7. Application state

Use a small explicit state machine:

```text
Disabled
   ↕ tray toggle
Idle
   ↓ hotkey down
Recording
   ↓ hotkey up
Transcribing
   ↓ transcript received
Cleaning
   ↓ cleaned text received
Pasting
   ↓ complete
Idle
```

Any stage can move to `Error`, display a short message, then return to `Idle`.

The controller must guarantee that only one dictation session exists at a time.

## 8. Component design

### 8.1 App host and tray menu

The WPF application starts without a normal main window and remains alive through the tray icon.

Tray menu:

```text
✓ Enabled
  Launch at login
  Reload settings
  Open settings folder
  Open README
──────────────
  Exit
```

Behaviour:

- `Enabled` installs or removes the keyboard hook.
- `Launch at login` writes or removes a per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value.
- `Reload settings` validates and reloads `settings.json` and `.env` without restarting.
- `Open settings folder` opens `%LOCALAPPDATA%\Dictate`.
- No graphical settings window is required.

### 8.2 Audio recorder

Use NAudio with WASAPI shared-mode capture from the current Windows default input device.

Recording rules:

- Use the default microphone unless `audio.deviceId` is set.
- Capture to memory, not a permanent file.
- Convert to mono, 16 kHz, 16-bit PCM WAV after recording.
- Keep a hard maximum duration to prevent accidental unlimited recording.
- Dispose the capture device and buffers in `finally` blocks.
- Never retain audio after the transcription attempt finishes.

A 90-second mono 16 kHz 16-bit recording is only a few megabytes, so an in-memory stream is sufficient and avoids temporary audio files.

### 8.3 Local transcription

Implement `WhisperCppTranscriber` using Whisper.net and the CPU whisper.cpp runtime.

Reasons:

- It still uses whisper.cpp.
- The model can stay loaded between dictations.
- There is no repeated process startup or repeated model loading.
- There is no P/Invoke layer to maintain directly.
- There is no localhost server process to supervise.

Behaviour:

- Load the configured model lazily on first use.
- Keep one factory/model instance alive until application exit.
- Process one recording at a time.
- Set the language to English by default; allow `auto` or another language in settings.
- Return plain text with no timestamps.
- Support cancellation on application exit.

Recommended bundled model:

- `ggml-small.en.bin`
- Approximately 466 MiB on disk and about 852 MB working memory according to whisper.cpp’s published model table.

Optional manual replacement:

- `medium.en` for greater accuracy on a machine with enough memory
- a quantised `small.en` model if installer size matters more than maximum accuracy

Do not build a model picker UI. The model is selected by path in `settings.json`.

### 8.4 Cloud transcription

Implement one `OpenAiCompatibleTranscriber` with provider-specific base URL, API key, and model name, plus an `AzureSpeechTranscriber` for Azure's Fast Transcription REST API.

Providers:

- `openai`
- `groq`
- `azure`

All receive the completed WAV recording through a multipart `POST` to their transcription endpoint. Keep OpenAI-compatible model names configurable because providers change their recommended models over time. Azure uses its region-scoped Speech resource key, region identifier, and locale.

Suggested initial values as of July 2026:

- OpenAI: `gpt-4o-transcribe`
- Groq: `whisper-large-v3-turbo`

No provider is selected automatically. The `transcription.provider` setting is authoritative.

Recommended failure policy:

- If a cloud request fails and `fallbackToLocal` is `true`, run the bundled local transcriber.
- If the required API key is missing, show a configuration error immediately rather than attempting a request.
- Use a bounded HTTP timeout.
- Do not repeatedly retry and create duplicate API charges.

### 8.5 Transcript cleanup

Define:

```csharp
interface ITextCleaner
{
    Task<string> CleanAsync(string transcript, CancellationToken cancellationToken);
}
```

Implement two cleaners.

#### `BasicTextCleaner`

Used for fully offline operation and as the failure fallback. It should only:

- Trim leading/trailing whitespace.
- Collapse repeated spaces.
- Remove immediately repeated filler tokens such as `um um` or `uh uh`.
- Remove a leading standalone `um`, `uh`, or `erm` when safe.
- Capitalise the first character when `style` is `sentence`.
- Add final punctuation only when `style` is `sentence` and the text does not already end with punctuation.

It must not broadly rewrite text because regex-based “smart” cleanup can silently alter meaning.

#### `LlmTextCleaner`

Use OpenAI or Groq according to settings. Send only the raw transcript and a fixed, short system prompt.

Recommended prompt:

```text
You clean voice dictation transcripts.

Return only the corrected text.
- Remove filler words and abandoned false starts only when meaning is unchanged.
- Fix punctuation, spacing, and obvious transcription mistakes.
- Preserve the speaker's wording, intent, names, numbers, URLs, and technical terms.
- Do not summarise, answer, explain, or add information.
- Match casing to the apparent dictation style. Use normal sentence case for prose,
  preserve intentional capitals, and keep short casual fragments natural.
```

Request settings:

- Low temperature or equivalent deterministic setting
- Small output limit based on input length
- No conversation history
- No tools
- No app context or surrounding text

Failure policy:

- On timeout, API error, malformed response, or missing key, run `BasicTextCleaner` and continue.
- Never discard a successful transcript merely because cleanup failed.

### 8.6 Text insertion

Use clipboard plus simulated `Ctrl+V` as the only insertion method in version 1.

Algorithm:

1. Confirm the original target window is still foreground.
2. Take a best-effort snapshot of the existing clipboard `IDataObject`.
3. Put the final text on the clipboard as Unicode text.
4. Immediately record the clipboard sequence number produced by that write as `ownedSequence`.
5. Send `Ctrl+V` with Win32 `SendInput`.
6. Wait a short configurable delay, initially 175 ms.
7. Restore the old clipboard only when:
   - clipboard restoration is enabled;
   - the current clipboard sequence still equals `ownedSequence`; and
   - the clipboard still contains the app’s inserted value.
8. If another application or the user changes the clipboard, do not overwrite that newer value.
9. If insertion cannot be confirmed or restoration fails, leave the dictated text on the clipboard and show `Copied`.

Clipboard access should retry a few times over a short period because another process may temporarily have it open.

#### Known Windows limitation

A normally running app cannot inject input into a target running at a higher integrity level. Therefore, pasting into an application running “as administrator” can fail. Do not make `Dictate` run elevated by default. Leave the result on the clipboard in this case.

### 8.7 Floating pill

Use one borderless WPF `Window`:

- Around 160 × 36 device-independent pixels
- Rounded corners
- Topmost
- No taskbar entry
- `ShowActivated = false`
- Not focusable
- Extended styles `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW`
- Optional click-through style
- Positioned at the bottom centre of the monitor containing the target window

States:

- `● Listening…`
- `◌ Transcribing…`
- `◌ Cleaning…`
- `✓ Pasted`
- `Copied`
- `No speech detected`
- A concise error such as `Microphone unavailable`

Do not add waveforms, animated audio visualisers, transcript previews, draggable positioning, or a full overlay framework.

## 9. Configuration

### Paths

Standard install:

```text
Application:
%LOCALAPPDATA%\Programs\Dictate\

User settings and secrets:
%LOCALAPPDATA%\Dictate\settings.json
%LOCALAPPDATA%\Dictate\.env

Logs:
%LOCALAPPDATA%\Dictate\logs\
```

The installer creates example configuration files only when they do not already exist, so upgrades do not overwrite local settings or keys. Relative paths such as `models/ggml-small.en.bin` resolve from the application directory, not from the settings directory.

### `settings.json`

```json
{
  "enabled": true,
  "hotkey": {
    "shortcut": "RightCtrl",
    "suppress": true
  },
  "audio": {
    "deviceId": "default",
    "maxSeconds": 90
  },
  "transcription": {
    "provider": "local",
    "language": "en",
    "fallbackToLocal": true,
    "localModelPath": "models/ggml-small.en.bin",
    "localThreads": 0,
    "openaiModel": "gpt-4o-transcribe",
    "groqModel": "whisper-large-v3-turbo",
    "azureRegion": "",
    "azureLocale": "en-US"
  },
  "cleanup": {
    "provider": "basic",
    "model": "",
    "style": "auto",
    "fallbackToBasic": true
  },
  "paste": {
    "restoreClipboard": true,
    "restoreDelayMs": 175
  },
  "launchAtLogin": false
}
```

Allowed values:

```text
transcription.provider = local | openai | groq | azure
cleanup.provider       = basic | openai | groq | none
cleanup.style          = auto | sentence | fragment
```

`localThreads = 0` means choose a sensible value from available logical processors, capped so the app does not consume every core.

### `.env`

```dotenv
OPENAI_API_KEY=
GROQ_API_KEY=
AZURE_SPEECH_KEY=
```

The `.env` file is intentionally simple and is not committed to source control. It is plain text, readable by the Windows user account. Do not add DPAPI or a credential vault unless this becomes a multi-user or distributed product.

### Validation

On startup and reload:

- Reject unknown providers.
- Reject an unparseable hotkey.
- Verify the local model exists when local transcription or local fallback is enabled.
- Verify the required API key exists for a selected cloud provider.
- Keep the previous valid in-memory settings if reload fails.
- Show a tray balloon and log only the configuration error, never secret values.

## 10. Suggested source layout

Keep the project flat and understandable:

```text
Dictate/
├─ src/Dictate/
│  ├─ App.xaml
│  ├─ App.xaml.cs
│  ├─ DictationController.cs
│  ├─ DictationState.cs
│  ├─ HotkeyHook.cs
│  ├─ HotkeyParser.cs
│  ├─ AudioRecorder.cs
│  ├─ Transcribers.cs
│  ├─ TextCleaners.cs
│  ├─ TextInserter.cs
│  ├─ PillWindow.xaml
│  ├─ PillWindow.xaml.cs
│  ├─ TrayMenu.cs
│  ├─ Settings.cs
│  └─ StartupRegistration.cs
├─ tests/Dictate.Tests/
│  ├─ HotkeyParserTests.cs
│  ├─ BasicTextCleanerTests.cs
│  ├─ SettingsTests.cs
│  └─ ProviderClientTests.cs
├─ installer/
│  └─ Dictate.iss
├─ models/
│  └─ ggml-small.en.bin
├─ README.md
├─ settings.example.json
└─ .env.example
```

Do not split this into multiple class-library projects. One app project and one test project are enough.

## 11. Error and fallback rules

| Failure | Behaviour |
|---|---|
| Hotkey registration fails | Disable dictation and show a tray error |
| Microphone unavailable | Stop, show `Microphone unavailable`, paste nothing |
| Recording is effectively silent | Show `No speech detected`, paste nothing |
| Cloud STT key missing | Show configuration error; use local only if explicitly configured as fallback |
| Cloud STT request fails | Use local fallback when enabled; otherwise show error |
| Local model missing | Show exact expected model path; do not download silently |
| LLM cleanup fails | Run basic cleanup and paste the transcript |
| Clipboard temporarily locked | Retry briefly |
| Paste blocked or target elevated | Leave result on clipboard and show `Copied` |
| Target focus changed | Leave result on clipboard and show `Copied — target changed` |
| Unexpected exception | Return to `Idle`; log exception metadata without audio or transcript |

The primary rule is: once transcription succeeds, cleanup or paste failures must not lose the text.

## 12. Privacy and security

### Local mode

With:

```text
transcription.provider = local
cleanup.provider = basic or none
```

- Audio remains in memory on the PC.
- Transcription runs through local whisper.cpp.
- No audio or transcript is sent over the network.
- No audio or transcript is saved after the operation.

### Cloud mode

- Cloud transcription sends the recorded audio to the selected transcription provider.
- Cloud cleanup sends the raw transcript to the selected LLM provider.
- The README must state this clearly next to configuration examples.

### Logging

Use a minimal rolling local log:

- Keep at most five small files.
- Log state changes, durations, provider names, and exception types.
- Never log audio, transcript text, clipboard contents, request bodies, API keys, or authentication headers.
- There is no remote logging, crash reporting, analytics, or telemetry.

### Keyboard hook

The hook sees system keyboard events because Windows requires that for a global hold shortcut. Its callback must inspect only enough information to recognise the configured shortcut. It must not persist, transmit, or expose unrelated key events.

## 13. Standalone packaging

### Build target

Target Windows 11 x64 only for version 1.

Publish a self-contained .NET build so the machine does not need a separately installed .NET runtime:

```powershell
dotnet publish .\src\Dictate\Dictate.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false
```

A folder publish is intentional. WPF, native whisper.cpp libraries, and the model are easier to package and diagnose as normal files. Inno Setup then produces one `Dictate-Setup.exe` containing the full folder.

### Installer behaviour

Use a per-user Inno Setup installer:

- Install to `%LOCALAPPDATA%\Programs\Dictate`.
- Require no administrator rights.
- Include the .NET runtime, native whisper.cpp runtime, and `small.en` model.
- Add Start menu shortcuts for `Dictate`, `README`, and `Uninstall`.
- Optionally enable launch at login during installation.
- Preserve `%LOCALAPPDATA%\Dictate` settings during upgrades and uninstall unless the user explicitly chooses to remove them.
- Do not install a Windows service, scheduled task, driver, shell extension, or browser extension.

### Code signing

Code signing is optional for a personal build, but an unsigned installer may trigger Microsoft Defender SmartScreen. The README should explain the warning; it must never instruct the user to disable Defender or SmartScreen globally.

## 14. README: Windows permissions and limitations

The requested “Accessibility” and “Input Monitoring” permissions are macOS concepts. Windows does not have equivalent user-facing permission switches for this ordinary desktop application.

The README should contain the following.

### Microphone access

On Windows 11:

1. Open **Settings**.
2. Go to **Privacy & security → Microphone**.
3. Turn on **Microphone access**.
4. Turn on **Let desktop apps access your microphone**.

Windows normally displays a microphone indicator in the notification area while the mic is active.

### Keyboard and paste access

There is no separate Accessibility or Input Monitoring permission to grant.

The app uses a global low-level keyboard hook to detect only its configured shortcut and `SendInput` to press `Ctrl+V`.

A non-elevated app cannot inject input into an application running as administrator. When that occurs, the dictated text remains on the clipboard for manual paste.

### `Fn` key

The physical `Fn` key is commonly handled by keyboard firmware and may be invisible to Windows. Configure `RightCtrl`, another chord, or remap a hardware button to `F13`.

### Launch at login

The tray toggle adds a per-user Windows startup entry. It can also be inspected or disabled under **Settings → Apps → Startup** or in Task Manager’s Startup Apps page.

### Antivirus warning

A global keyboard hook and simulated paste are legitimate requirements for this application, but unsigned personal utilities can attract extra scrutiny. Build from source or sign the release where practical. Do not request antivirus exclusions as part of normal installation.

## 15. Minimum acceptance criteria

Version 1 is complete when all of the following work:

1. The app installs and starts without requiring .NET, Python, Docker, Node.js, or a separate model download.
2. The tray menu can enable/disable dictation and toggle launch at login.
3. Holding the configured shortcut starts microphone capture and shows the pill without stealing focus.
4. Releasing it stops capture and runs local whisper.cpp transcription.
5. Local/basic mode works with the network disconnected.
6. OpenAI, Groq, and Azure Speech transcription can each be selected in `settings.json` and read their key from `.env`.
7. OpenAI or Groq LLM cleanup can be selected independently.
8. LLM cleanup failure falls back to basic cleanup.
9. Text pastes correctly into at least Notepad, Edge/Chrome, Outlook, Teams, and VS Code when those applications are not elevated.
10. Ordinary text clipboard contents are restored after paste.
11. Focus changes and elevated targets leave the result safely on the clipboard.
12. No audio, transcript, clipboard content, or API key appears in logs.
13. Exiting the app removes the keyboard hook, releases the microphone, disposes the Whisper model, and removes the pill.

## 16. Implementation order

Keep development in five small slices.

### Slice 1 — shell

- WPF no-main-window app
- Tray icon and menu
- Settings loader
- Launch-at-login toggle
- Recording pill

### Slice 2 — hold-to-talk and audio

- Hotkey parser
- Low-level keyboard hook
- WASAPI recording
- State machine and cancellation

### Slice 3 — local end-to-end path

- Whisper.net/whisper.cpp runtime
- Bundled `small.en` model
- Basic cleaner
- Clipboard paste and restore

At this point the application is already useful and fully offline.

### Slice 4 — cloud options

- OpenAI-compatible and Azure Speech transcription clients
- OpenAI/Groq LLM cleaner
- `.env` loading
- Timeouts and fallbacks

### Slice 5 — release

- Installer
- README permissions and privacy notes
- Acceptance tests
- Manual smoke testing in common applications

## 17. Deferred improvements only if needed

Do not implement these pre-emptively:

- Local llama.cpp cleanup
- CUDA or Vulkan acceleration
- Auto model downloads
- A hotkey-capture settings UI
- Multiple style profiles
- Per-application behaviour
- Voice activity detection before transcription
- Streaming partial transcripts
- Direct Unicode keystroke typing fallback
- Clipboard-history integration
- Automatic updater

## 18. Reference material

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [SetWindowsHookEx and `WH_KEYBOARD_LL`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexa)
- [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [Windows clipboard overview](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard)
- [Run and RunOnce startup keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
- [Windows microphone permissions](https://support.microsoft.com/en-us/windows/privacy/turn-on-app-permissions-for-your-microphone-in-windows)
- [whisper.cpp](https://github.com/ggml-org/whisper.cpp)
- [Whisper.net](https://github.com/sandrohanea/whisper.net)
- [OpenAI file transcription](https://developers.openai.com/api/docs/guides/speech-to-text)
- [Groq speech-to-text](https://console.groq.com/docs/speech-to-text)
- [Azure Speech fast transcription](https://learn.microsoft.com/azure/ai-services/speech-service/fast-transcription-create)

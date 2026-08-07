# Whisper models

Local dictation uses `ggml-small.en.bin` (approximately 466 MiB). Local meeting
transcription uses `ggml-medium.en.bin` (approximately 1.5 GiB). Both binaries
are intentionally excluded from Git.

Download the pinned model used by release builds:

```powershell
.\scripts\Get-WhisperModel.ps1
.\scripts\Get-WhisperModel.ps1 -Model medium.en
```

The script downloads the official whisper.cpp models and verifies the pinned
SHA-1 values (`db8a495a91d927739e50b3fc1cc4c6b8f6c2d022` for `small.en` and
`8c30f0e44ce9560643ebd10bbe50cd20eafd3723` for `medium.en`) before moving
them into place. Whispdows itself never downloads models silently.

# Whisper model

The application expects `ggml-small.en.bin` in this directory for local transcription. The binary is intentionally excluded from Git because it is approximately 466 MiB.

Download the pinned model used by release builds:

```powershell
.\scripts\Get-WhisperModel.ps1
```

The script downloads the official whisper.cpp `small.en` model and verifies SHA-1 `db8a495a91d927739e50b3fc1cc4c6b8f6c2d022` before moving it into place. Dictate itself never downloads models silently.

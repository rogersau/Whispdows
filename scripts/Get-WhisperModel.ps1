[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\models\ggml-small.en.bin')
)

$ErrorActionPreference = 'Stop'

$modelUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin'
$expectedSha1 = 'db8a495a91d927739e50b3fc1cc4c6b8f6c2d022'
$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
$destinationDirectory = Split-Path -Parent $resolvedDestination
$temporaryPath = "$resolvedDestination.download"

if (Test-Path -LiteralPath $resolvedDestination) {
    $existingHash = (Get-FileHash -LiteralPath $resolvedDestination -Algorithm SHA1).Hash.ToLowerInvariant()
    if ($existingHash -eq $expectedSha1) {
        Write-Host "Whisper model is already present and verified: $resolvedDestination"
        return
    }

    throw "A model exists at '$resolvedDestination' but its SHA-1 does not match the pinned small.en model."
}

New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null

$curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue
if ($curl) {
    & $curl.Source `
        --fail `
        --location `
        --silent `
        --show-error `
        --continue-at - `
        --output $temporaryPath `
        $modelUrl
    if ($LASTEXITCODE -ne 0) {
        throw "Model download failed with exit code $LASTEXITCODE. The partial download was preserved for resume."
    }
}
else {
    if (Test-Path -LiteralPath $temporaryPath) {
        throw "A partial model download exists at '$temporaryPath'. Install curl.exe to resume it, or remove it and retry."
    }

    Invoke-WebRequest -Uri $modelUrl -OutFile $temporaryPath
}

$downloadedHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA1).Hash.ToLowerInvariant()
if ($downloadedHash -ne $expectedSha1) {
    Remove-Item -LiteralPath $temporaryPath -Force
    throw "Downloaded model hash '$downloadedHash' does not match expected SHA-1 '$expectedSha1'."
}

Move-Item -LiteralPath $temporaryPath -Destination $resolvedDestination
Write-Host "Downloaded and verified Whisper small.en model: $resolvedDestination"

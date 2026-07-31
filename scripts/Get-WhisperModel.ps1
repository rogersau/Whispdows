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
        exit 0
    }

    throw "A model exists at '$resolvedDestination' but its SHA-1 does not match the pinned small.en model."
}

New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null

try {
    Invoke-WebRequest -Uri $modelUrl -OutFile $temporaryPath
    $downloadedHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA1).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $expectedSha1) {
        throw "Downloaded model hash '$downloadedHash' does not match expected SHA-1 '$expectedSha1'."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $resolvedDestination
    Write-Host "Downloaded and verified Whisper small.en model: $resolvedDestination"
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

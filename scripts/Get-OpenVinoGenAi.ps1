[CmdletBinding()]
param(
    [string]$Destination =
        (Join-Path $env:LOCALAPPDATA `
            'Whispdows\build-cache\openvino-genai-2026.2.0')
)

$ErrorActionPreference = 'Stop'

$archiveUrl =
    'https://storage.openvinotoolkit.org/repositories/openvino_genai/packages/2026.2/windows/openvino_genai_windows_2026.2.0.0_x86_64.zip'
$expectedArchiveSha256 =
    'ca6a85fc5c410329ab9f439b829a2660b76bed670038d96e6e247ee18657d2dc'
$archiveRootName = 'openvino_genai_windows_2026.2.0.0_x86_64'
$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
$releaseDirectory = Join-Path $resolvedDestination 'runtime\bin\intel64\Release'
$tbbDirectory = Join-Path $resolvedDestination 'runtime\3rdparty\tbb\bin'
$requiredFiles = @(
    (Join-Path $releaseDirectory 'openvino_genai_c.dll'),
    (Join-Path $releaseDirectory 'openvino_genai.dll'),
    (Join-Path $releaseDirectory 'openvino.dll'),
    (Join-Path $releaseDirectory 'openvino_tokenizers.dll'),
    (Join-Path $releaseDirectory 'openvino_intel_npu_plugin.dll'),
    (Join-Path $releaseDirectory 'openvino_intel_npu_compiler.dll'),
    (Join-Path $releaseDirectory 'openvino_intel_gpu_plugin.dll'),
    (Join-Path $tbbDirectory 'tbb12.dll')
)

if (@($requiredFiles | Where-Object {
            -not (Test-Path -LiteralPath $_ -PathType Leaf)
        }).Count -eq 0) {
    Write-Host "OpenVINO GenAI 2026.2.0 is already present: $resolvedDestination"
    return
}

if (Test-Path -LiteralPath $resolvedDestination) {
    throw "OpenVINO GenAI files at '$resolvedDestination' are incomplete. Remove that cache directory and retry."
}

$archivePath = "$resolvedDestination.zip.download"
$archiveDirectory = Split-Path -Parent $archivePath
New-Item -ItemType Directory -Force -Path $archiveDirectory | Out-Null

$archiveIsComplete = (Test-Path -LiteralPath $archivePath -PathType Leaf) -and
    ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq
        $expectedArchiveSha256)
if (-not $archiveIsComplete) {
    $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue
    if (-not $curl) {
        throw 'curl.exe is required to download the resumable OpenVINO GenAI archive.'
    }

    & $curl.Source `
        --fail `
        --location `
        --silent `
        --show-error `
        --continue-at - `
        --output $archivePath `
        $archiveUrl
    if ($LASTEXITCODE -ne 0) {
        throw "OpenVINO GenAI download failed with exit code $LASTEXITCODE. The partial archive was preserved."
    }
}

$archiveHash = (
    Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
).Hash.ToLowerInvariant()
if ($archiveHash -ne $expectedArchiveSha256) {
    throw "Downloaded OpenVINO GenAI archive hash '$archiveHash' does not match the pinned release."
}

$stagingDirectory = "$resolvedDestination.extracting"
if (Test-Path -LiteralPath $stagingDirectory) {
    throw "A stale extraction directory exists at '$stagingDirectory'. Remove it and retry."
}

New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingDirectory
$extractedRoot = Join-Path $stagingDirectory $archiveRootName
if (-not (Test-Path -LiteralPath $extractedRoot -PathType Container)) {
    throw "The OpenVINO GenAI archive did not contain '$archiveRootName'."
}

Move-Item -LiteralPath $extractedRoot -Destination $resolvedDestination
Remove-Item -LiteralPath $stagingDirectory -Force

$missingFile = $requiredFiles | Where-Object {
    -not (Test-Path -LiteralPath $_ -PathType Leaf)
} | Select-Object -First 1
if ($missingFile) {
    throw "The extracted OpenVINO GenAI release is missing '$missingFile'."
}

Write-Host "Downloaded and verified OpenVINO GenAI 2026.2.0: $resolvedDestination"

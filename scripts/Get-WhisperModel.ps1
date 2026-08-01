[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\models\ggml-small.en.bin'),
    [string]$AcceleratedDestination =
        (Join-Path $PSScriptRoot '..\models\whisper-base.en-int8-ov'),
    [Alias('SkipEncoder')]
    [switch]$SkipAccelerated
)

$ErrorActionPreference = 'Stop'

$cpuModelUrl =
    'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin'
$expectedCpuModelSha1 = 'db8a495a91d927739e50b3fc1cc4c6b8f6c2d022'
$acceleratedModelRevision = '3b292a83752fbfcad0bd6384bcf71d0b1fc4fe74'
$acceleratedModelFiles = [ordered]@{
    'added_tokens.json' = '417dfa8a5cba6f5c9de04fcc1163ff2e1e3177d99e86dcc1ef938006ac809dc2'
    'config.json' = '9798a5038a37831952b16e87905be828dde8a7d5a392da5e46177ac5f1c57ebe'
    'generation_config.json' = '7d1e75339aaade1581424f54c35700120a14cc4273363216549db9cc0deaf88b'
    'merges.txt' = '84809de545b2f3e79275acfaefa4af5055438ddb13d9eed9cff02eacf5cc19fc'
    'normalizer.json' = '6c40cc36b4bb9c5aa8be0ff9023ea4e78a3ab718b3490a1d9cd3cb7ec56f130f'
    'openvino_config.json' = 'c40ec7289d70b49c53bf9c4eaaaf378196b552f6dcf708ffd971ed2565d485b6'
    'openvino_decoder_model.bin' = 'fa97c0aa3989311aca9eeaa72997d2d14ae112b0c8a54d055a4d7dca88bca1e9'
    'openvino_decoder_model.xml' = '168f3b87f21bee26dc843b1040b278d1778ced8a3359f009f76d3c76761ede8a'
    'openvino_detokenizer.bin' = '7045e2ab69c216fa4ef1d129fcce4d36bcf5583f01b823279278b52626238e0c'
    'openvino_detokenizer.xml' = '54a7072d9ecb1d55b760d41ca91c547ec8321c40b11ff0b7f308231fca427286'
    'openvino_encoder_model.bin' = 'f2efb087f58680a7d7cc9916a3ab8712e776ddf579b7dcce38945da08441609b'
    'openvino_encoder_model.xml' = '11d6061d6b0a24ae4ae648931d5a19e07f20b47559a464d3d69b456c4290605b'
    'openvino_tokenizer.bin' = '8c9def49b61ff1cdd929b1c4b035e6714f69157587ada9cbb61cb15d6248ea2b'
    'openvino_tokenizer.xml' = 'dfe27a088d3f7a86c6be6afdc53c2a68ee55aa36ab078916379b8e9ce8fd458d'
    'preprocessor_config.json' = 'e4449dfed25a2b4c054b6103753daebad3cfab42c1ac0421b3e5c621a4de22e1'
    'special_tokens_map.json' = 'fa3edc8600f1afc856d30133f3fbfacdbb21bbf60d474778a6c56976d8377239'
    'tokenizer_config.json' = '67425d1207b17d632b408a3451dac50d24eb719cb0d179cf8b5e6bcf2a71046c'
    'tokenizer.json' = '344013f1638b42f03d6addd41739f34a812c8513efde65eb8d0b52b72a46fd10'
    'vocab.json' = '3ba3c3109ff33976c4bd966589c11ee14fcaa1f4c9e5e154c2ed7f99d80709e7'
}

function Invoke-ResumableDownload {
    param(
        [Parameter(Mandatory)]
        [string]$Uri,
        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    $curl = Get-Command 'curl.exe' -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source `
            --fail `
            --location `
            --silent `
            --show-error `
            --continue-at - `
            --output $DestinationPath `
            $Uri
        if ($LASTEXITCODE -ne 0) {
            throw "Download failed with exit code $LASTEXITCODE. The partial file was preserved at '$DestinationPath'."
        }
        return
    }

    if (Test-Path -LiteralPath $DestinationPath) {
        throw "A partial download exists at '$DestinationPath'. Install curl.exe to resume it, or remove it and retry."
    }

    Invoke-WebRequest -Uri $Uri -OutFile $DestinationPath
}

$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $resolvedDestination -PathType Leaf) {
    $existingHash = (
        Get-FileHash -LiteralPath $resolvedDestination -Algorithm SHA1
    ).Hash.ToLowerInvariant()
    if ($existingHash -ne $expectedCpuModelSha1) {
        throw "A model exists at '$resolvedDestination' but its SHA-1 does not match the pinned small.en model."
    }

    Write-Host "Whisper CPU model is already present and verified: $resolvedDestination"
}
else {
    New-Item -ItemType Directory -Force -Path (
        Split-Path -Parent $resolvedDestination
    ) | Out-Null
    $temporaryPath = "$resolvedDestination.download"
    Invoke-ResumableDownload -Uri $cpuModelUrl -DestinationPath $temporaryPath
    $downloadedHash = (
        Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA1
    ).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $expectedCpuModelSha1) {
        throw "Downloaded CPU model hash '$downloadedHash' does not match '$expectedCpuModelSha1'."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $resolvedDestination
    Write-Host "Downloaded and verified Whisper CPU model: $resolvedDestination"
}

if ($SkipAccelerated) {
    return
}

$resolvedAcceleratedDestination = [System.IO.Path]::GetFullPath(
    $AcceleratedDestination)
New-Item -ItemType Directory -Force -Path $resolvedAcceleratedDestination |
    Out-Null

foreach ($modelFile in $acceleratedModelFiles.GetEnumerator()) {
    $destinationPath = Join-Path $resolvedAcceleratedDestination $modelFile.Key
    if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        $existingHash = (
            Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        if ($existingHash -ne $modelFile.Value) {
            throw "The accelerated model file '$destinationPath' does not match the pinned OpenVINO model."
        }
        continue
    }

    $temporaryPath = "$destinationPath.download"
    $modelFileUrl =
        "https://huggingface.co/OpenVINO/whisper-base.en-int8-ov/resolve/$acceleratedModelRevision/$($modelFile.Key)?download=true"
    Invoke-ResumableDownload -Uri $modelFileUrl -DestinationPath $temporaryPath
    $downloadedHash = (
        Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $modelFile.Value) {
        throw "Downloaded accelerated model file '$($modelFile.Key)' does not match its pinned SHA-256."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $destinationPath
}

Write-Host (
    "OpenVINO GenAI Whisper model is present and verified at revision " +
    "$acceleratedModelRevision`: $resolvedAcceleratedDestination")

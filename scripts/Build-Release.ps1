[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$SkipModelDownload,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot "publish\$RuntimeIdentifier"))
$installerDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'installer'))
$platform = if ($RuntimeIdentifier -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$modelPath = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'models\ggml-small.en.bin'))
$acceleratedModelDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'models\whisper-base.en-int8-ov'))
$openVinoCacheDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA `
        'Whispdows\build-cache\openvino-genai-2026.2.0'))
$openVinoReleaseDirectory = Join-Path $openVinoCacheDirectory 'runtime\bin\intel64\Release'
$openVinoTbbDirectory = Join-Path $openVinoCacheDirectory 'runtime\3rdparty\tbb\bin'
$openVinoLicensingDirectory = Join-Path $openVinoCacheDirectory 'docs\licensing'
$expectedModelSha1 = 'db8a495a91d927739e50b3fc1cc4c6b8f6c2d022'
$expectedAcceleratedEncoderSha256 = 'f2efb087f58680a7d7cc9916a3ab8712e776ddf579b7dcce38945da08441609b'
$expectedAcceleratedDecoderSha256 = 'fa97c0aa3989311aca9eeaa72997d2d14ae112b0c8a54d055a4d7dca88bca1e9'
$dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue
$dotnetPath = if ($dotnetCommand) {
    $dotnetCommand.Source
}
else {
    Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
}
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw 'The .NET SDK could not be found. Install .NET 10 before building Whispdows.'
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Parent
    )

    $relative = [System.IO.Path]::GetRelativePath($Parent, $Path)
    if ([System.IO.Path]::IsPathRooted($relative) -or
        $relative -eq '..' -or
        $relative.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)")) {
        throw "Refusing to modify '$Path' because it is outside '$Parent'."
    }
}

function Assert-PortableExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [int]$ExpectedMachine
    )

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' is not a Windows portable executable."
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
            throw "'$Path' has an invalid PE header offset."
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' has an invalid PE signature."
        }

        $machine = $reader.ReadUInt16()
        if ($machine -ne $ExpectedMachine) {
            throw "'$Path' targets PE machine 0x$($machine.ToString('X4')); expected 0x$($ExpectedMachine.ToString('X4'))."
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

Assert-ChildPath -Path $publishDirectory -Parent $artifactsRoot
Assert-ChildPath -Path $installerDirectory -Parent $artifactsRoot

if (-not $SkipModelDownload) {
    $modelArguments = @{
        Destination = $modelPath
        AcceleratedDestination = $acceleratedModelDirectory
    }
    if ($RuntimeIdentifier -eq 'win-arm64') {
        $modelArguments.SkipAccelerated = $true
    }

    & (Join-Path $PSScriptRoot 'Get-WhisperModel.ps1') @modelArguments
    if ($RuntimeIdentifier -eq 'win-x64') {
        & (Join-Path $PSScriptRoot 'Get-OpenVinoGenAi.ps1') `
            -Destination $openVinoCacheDirectory
    }
}

if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) {
    throw "The release model is missing at '$modelPath'. Run Get-WhisperModel.ps1 first."
}

$modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA1).Hash.ToLowerInvariant()
if ($modelHash -ne $expectedModelSha1) {
    throw "The release model hash does not match the pinned Whisper small.en model."
}

if ($RuntimeIdentifier -eq 'win-x64') {
    foreach ($acceleratedAsset in @(
        @{
            Path = (Join-Path $acceleratedModelDirectory 'openvino_encoder_model.bin')
            Hash = $expectedAcceleratedEncoderSha256
        },
        @{
            Path = (Join-Path $acceleratedModelDirectory 'openvino_decoder_model.bin')
            Hash = $expectedAcceleratedDecoderSha256
        }
    )) {
        if (-not (Test-Path -LiteralPath $acceleratedAsset.Path -PathType Leaf)) {
            throw "The OpenVINO GenAI model is missing '$($acceleratedAsset.Path)'. Run Get-WhisperModel.ps1 first."
        }

        $assetHash = (Get-FileHash -LiteralPath $acceleratedAsset.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($assetHash -ne $acceleratedAsset.Hash) {
            throw "The OpenVINO GenAI model asset '$($acceleratedAsset.Path)' does not match the pinned model."
        }
    }

    if (-not (Test-Path -LiteralPath (
            Join-Path $openVinoReleaseDirectory 'openvino_genai_c.dll'
        ) -PathType Leaf)) {
        throw "OpenVINO GenAI 2026.2.0 is missing. Run Get-OpenVinoGenAi.ps1 first."
    }
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
Push-Location $repositoryRoot
try {
    & $dotnetPath publish .\src\Whispdows\Whispdows.csproj `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        --output $publishDirectory `
        -p:Platform=$platform `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$publishedModelsDirectory = Join-Path $publishDirectory 'models'
New-Item -ItemType Directory -Force -Path $publishedModelsDirectory |
    Out-Null
Copy-Item -LiteralPath $modelPath -Destination $publishedModelsDirectory
if ($RuntimeIdentifier -eq 'win-x64') {
    $publishedAcceleratedDirectory = Join-Path $publishedModelsDirectory `
        'whisper-base.en-int8-ov'
    New-Item -ItemType Directory -Force `
        -Path $publishedAcceleratedDirectory | Out-Null
    Get-ChildItem -LiteralPath $acceleratedModelDirectory -File |
        Copy-Item -Destination $publishedAcceleratedDirectory
}

if ($RuntimeIdentifier -eq 'win-x64') {
    $workerPublishDirectory = Join-Path $publishDirectory 'workers\openvino-genai'
    $workerProject = Join-Path $repositoryRoot `
        'src\Whispdows.InferenceWorker\Whispdows.InferenceWorker.csproj'
    & $dotnetPath publish $workerProject `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --output $workerPublishDirectory `
        -p:Platform=x64 `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:BundleModels=false
    if ($LASTEXITCODE -ne 0) {
        throw "OpenVINO GenAI worker publish failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $openVinoReleaseDirectory -File |
        Copy-Item -Destination $workerPublishDirectory
    Get-ChildItem -LiteralPath $openVinoTbbDirectory -File |
        Where-Object Name -NotLike '*debug*' |
        Copy-Item -Destination $workerPublishDirectory
    $workerLicensingDirectory = Join-Path $workerPublishDirectory `
        'licenses\openvino-genai'
    New-Item -ItemType Directory -Force -Path $workerLicensingDirectory |
        Out-Null
    Get-ChildItem -LiteralPath $openVinoLicensingDirectory -File |
        Copy-Item -Destination $workerLicensingDirectory
}

foreach ($architecture in @('win-x86', 'win-x64', 'win-arm64') |
        Where-Object { $_ -ne $RuntimeIdentifier }) {
    $extraRuntime = [System.IO.Path]::GetFullPath(
        (Join-Path $publishDirectory "runtimes\$architecture"))
    Assert-ChildPath -Path $extraRuntime -Parent $publishDirectory
    if (Test-Path -LiteralPath $extraRuntime) {
        Remove-Item -LiteralPath $extraRuntime -Recurse -Force
    }
}

$publishedModel = Join-Path $publishDirectory 'models\ggml-small.en.bin'
$publishedAcceleratedModelDirectory = Join-Path $publishDirectory `
    'models\whisper-base.en-int8-ov'
$nativeRuntimeDirectory = Join-Path $publishDirectory "runtimes\$RuntimeIdentifier"
$nativeRuntimeFiles = @(
    'ggml-base-whisper.dll',
    'ggml-cpu-whisper.dll',
    'ggml-whisper.dll',
    'whisper.dll'
) | ForEach-Object { Join-Path $nativeRuntimeDirectory $_ }
$windowsMlRuntimeFiles = @(
    'Microsoft.AI.Foundry.Local.Core.dll',
    'Microsoft.Windows.AI.MachineLearning.dll',
    'onnxruntime.dll',
    'onnxruntime-genai.dll'
) | ForEach-Object { Join-Path $publishDirectory $_ }
$acceleratedRuntimeFiles = if ($RuntimeIdentifier -eq 'win-x64') {
    $workerDirectory = Join-Path $publishDirectory 'workers\openvino-genai'
    @(
        (Join-Path $workerDirectory 'Whispdows.InferenceWorker.exe'),
        (Join-Path $workerDirectory 'openvino_genai_c.dll'),
        (Join-Path $workerDirectory 'openvino_genai.dll'),
        (Join-Path $workerDirectory 'openvino.dll'),
        (Join-Path $workerDirectory 'openvino_tokenizers.dll'),
        (Join-Path $workerDirectory 'openvino_intel_npu_plugin.dll'),
        (Join-Path $workerDirectory 'openvino_intel_gpu_plugin.dll'),
        (Join-Path $workerDirectory 'openvino_intel_npu_compiler.dll'),
        (Join-Path $workerDirectory 'tbb12.dll'),
        (Join-Path $workerDirectory 'licenses\openvino-genai\LICENSE-GENAI'),
        (Join-Path $publishedAcceleratedModelDirectory 'openvino_encoder_model.bin'),
        (Join-Path $publishedAcceleratedModelDirectory 'openvino_decoder_model.bin')
    )
}
else {
    @()
}
foreach ($requiredFile in @(
    (Join-Path $publishDirectory 'Whispdows.exe'),
    (Join-Path $publishDirectory 'README.md'),
    (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.md'),
    (Join-Path $publishDirectory 'settings.example.json'),
    $publishedModel,
    $nativeRuntimeFiles,
    $windowsMlRuntimeFiles,
    $acceleratedRuntimeFiles
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Published release is missing '$requiredFile'."
    }
}

$unexpectedRuntimeDirectories = @(
    Get-ChildItem -LiteralPath (Join-Path $publishDirectory 'runtimes') -Directory |
        Where-Object Name -ne $RuntimeIdentifier
)
if ($unexpectedRuntimeDirectories.Count -gt 0) {
    throw "Published release contains unexpected runtime directories: $($unexpectedRuntimeDirectories.Name -join ', ')."
}

$expectedMachine = if ($RuntimeIdentifier -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
Assert-PortableExecutable -Path (Join-Path $publishDirectory 'Whispdows.exe') -ExpectedMachine $expectedMachine
foreach ($nativeRuntimeFile in $nativeRuntimeFiles) {
    Assert-PortableExecutable -Path $nativeRuntimeFile -ExpectedMachine $expectedMachine
}
foreach ($nativeRuntimeFile in $acceleratedRuntimeFiles |
        Where-Object { [System.IO.Path]::GetExtension($_) -eq '.dll' }) {
    Assert-PortableExecutable -Path $nativeRuntimeFile -ExpectedMachine $expectedMachine
}

$publishedModelHash = (
    Get-FileHash -LiteralPath $publishedModel -Algorithm SHA1
).Hash.ToLowerInvariant()
if ($publishedModelHash -ne $expectedModelSha1) {
    throw "The published model hash does not match the pinned model."
}

if ($RuntimeIdentifier -eq 'win-x64') {
    $publishedEncoder = Join-Path $publishedAcceleratedModelDirectory `
        'openvino_encoder_model.bin'
    $publishedDecoder = Join-Path $publishedAcceleratedModelDirectory `
        'openvino_decoder_model.bin'
    if ((Get-FileHash -LiteralPath $publishedEncoder -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $expectedAcceleratedEncoderSha256 -or
        (Get-FileHash -LiteralPath $publishedDecoder -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $expectedAcceleratedDecoderSha256) {
        throw 'The published OpenVINO GenAI model does not match the pinned model.'
    }
}

if (-not $SkipInstaller) {
    if ($RuntimeIdentifier -ne 'win-x64') {
        throw "The current Inno Setup installer targets x64. Use -SkipInstaller when publishing $RuntimeIdentifier."
    }

    $compiler = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    $compilerPath = if ($compiler) {
        $compiler.Source
    }
    else {
        @(
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 7\ISCC.exe'
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }

    if (-not $compilerPath) {
        throw "Inno Setup Compiler (ISCC.exe) was not found. Install Inno Setup or use -SkipInstaller."
    }

    New-Item -ItemType Directory -Force -Path $installerDirectory | Out-Null
    & $compilerPath "/DMyAppVersion=$Version" (Join-Path $repositoryRoot 'installer\Whispdows.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }

    $installerPath = Join-Path $installerDirectory 'Whispdows-Setup.exe'
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Inno Setup completed without producing '$installerPath'."
    }

    Write-Host "Installer: $installerPath"
}

Write-Host "Published release: $publishDirectory"

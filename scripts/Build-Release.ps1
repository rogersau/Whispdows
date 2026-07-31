[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [switch]$SkipModelDownload,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'publish\win-x64'))
$installerDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'installer'))
$models = @(
    @{
        Name = 'small.en'
        Path = [System.IO.Path]::GetFullPath(
            (Join-Path $repositoryRoot 'models\ggml-small.en.bin'))
        Sha1 = 'db8a495a91d927739e50b3fc1cc4c6b8f6c2d022'
    },
    @{
        Name = 'medium.en'
        Path = [System.IO.Path]::GetFullPath(
            (Join-Path $repositoryRoot 'models\ggml-medium.en.bin'))
        Sha1 = '8c30f0e44ce9560643ebd10bbe50cd20eafd3723'
    }
)

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

function Assert-X64PortableExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Path
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
        if ($machine -ne 0x8664) {
            throw "'$Path' targets PE machine 0x$($machine.ToString('X4')); expected x64 (0x8664)."
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
    foreach ($model in $models) {
        & (Join-Path $PSScriptRoot 'Get-WhisperModel.ps1') `
            -Model $model.Name `
            -Destination $model.Path
    }
}

foreach ($model in $models) {
    if (-not (Test-Path -LiteralPath $model.Path -PathType Leaf)) {
        throw "The release model is missing at '$($model.Path)'. Run Get-WhisperModel.ps1 -Model $($model.Name) first."
    }

    $modelHash = (
        Get-FileHash -LiteralPath $model.Path -Algorithm SHA1
    ).Hash.ToLowerInvariant()
    if ($modelHash -ne $model.Sha1) {
        throw "The release model hash does not match the pinned Whisper $($model.Name) model."
    }
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
Push-Location $repositoryRoot
try {
    dotnet publish .\src\Whispdows\Whispdows.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --output $publishDirectory `
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

foreach ($architecture in @('win-x86', 'win-arm64')) {
    $extraRuntime = [System.IO.Path]::GetFullPath(
        (Join-Path $publishDirectory "runtimes\$architecture"))
    Assert-ChildPath -Path $extraRuntime -Parent $publishDirectory
    if (Test-Path -LiteralPath $extraRuntime) {
        Remove-Item -LiteralPath $extraRuntime -Recurse -Force
    }
}

$publishedModels = @(
    @{
        Name = 'small.en'
        Path = Join-Path $publishDirectory 'models\ggml-small.en.bin'
        Sha1 = 'db8a495a91d927739e50b3fc1cc4c6b8f6c2d022'
    },
    @{
        Name = 'medium.en'
        Path = Join-Path $publishDirectory 'models\ggml-medium.en.bin'
        Sha1 = '8c30f0e44ce9560643ebd10bbe50cd20eafd3723'
    }
)
$nativeRuntimeDirectory = Join-Path $publishDirectory 'runtimes\win-x64'
$nativeRuntimeFiles = @(
    'ggml-base-whisper.dll',
    'ggml-cpu-whisper.dll',
    'ggml-whisper.dll',
    'whisper.dll'
) | ForEach-Object { Join-Path $nativeRuntimeDirectory $_ }
foreach ($requiredFile in @(
    (Join-Path $publishDirectory 'Whispdows.exe'),
    (Join-Path $publishDirectory 'README.md'),
    (Join-Path $publishDirectory 'settings.example.json'),
    $publishedModels.Path,
    $nativeRuntimeFiles
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Published release is missing '$requiredFile'."
    }
}

$unexpectedRuntimeDirectories = @(
    Get-ChildItem -LiteralPath (Join-Path $publishDirectory 'runtimes') -Directory |
        Where-Object Name -ne 'win-x64'
)
if ($unexpectedRuntimeDirectories.Count -gt 0) {
    throw "Published release contains unexpected runtime directories: $($unexpectedRuntimeDirectories.Name -join ', ')."
}

Assert-X64PortableExecutable -Path (Join-Path $publishDirectory 'Whispdows.exe')
foreach ($nativeRuntimeFile in $nativeRuntimeFiles) {
    Assert-X64PortableExecutable -Path $nativeRuntimeFile
}

foreach ($model in $publishedModels) {
    $publishedModelHash = (
        Get-FileHash -LiteralPath $model.Path -Algorithm SHA1
    ).Hash.ToLowerInvariant()
    if ($publishedModelHash -ne $model.Sha1) {
        throw "The published $($model.Name) model hash does not match the pinned model."
    }
}

if (-not $SkipInstaller) {
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

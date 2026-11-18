param(
    [string]$InnoCompiler = 'ISCC.exe',
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $workspace 'src/App/App.csproj'
[xml]$buildProperties = Get-Content -Raw -LiteralPath (Join-Path $workspace 'Directory.Build.props')
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must specify a release version.' }
$output = Join-Path $workspace 'artifacts/release'
$null = New-Item -ItemType Directory -Force -Path $output
$output = (Resolve-Path -LiteralPath $output).Path
$inno = (Get-Command $InnoCompiler -CommandType Application).Source
if (-not $DotnetPath) {
    $localSdk = Join-Path $workspace '.dotnet/dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
}

$stagingName = '.staging-' + [Guid]::NewGuid().ToString('N')
$staging = Join-Path $output $stagingName
$payload = Join-Path $staging 'payload'
$null = New-Item -ItemType Directory -Path $payload -Force

try {
    & $DotnetPath publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -o $payload
    if ($LASTEXITCODE -ne 0) { throw ".NET publish failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath (Join-Path $workspace 'README.md') -Destination $payload

    $installer = 'VRCVoiceTranslator-Setup-win-x64.exe'
    $portable = 'VRCVoiceTranslator-Portable-win-x64.zip'
    & $inno '/Qp' "/DPayloadDir=$payload" "/DAppVersion=$version" "/O$staging" (Join-Path $PSScriptRoot 'VRCVoiceTranslator.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath (Join-Path $staging $portable) -CompressionLevel Optimal
    foreach ($file in @($installer, $portable)) {
        $destination = Join-Path $output $file
        Move-Item -LiteralPath (Join-Path $staging $file) -Destination $destination -Force
        Get-Item -LiteralPath $destination
    }
}
finally {
    $resolved = (Resolve-Path -LiteralPath $staging).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $output -or [IO.Path]::GetFileName($resolved) -ne $stagingName) {
        throw "Unexpected staging directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

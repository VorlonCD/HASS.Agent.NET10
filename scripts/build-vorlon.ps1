[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceCommit)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $actualCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $SourceCommit) {
        throw 'The checkout must match the full reviewed SourceCommit.'
    }
    $dirty = & git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit or remove local changes before building a traceable installer.' }

    $project = Join-Path $repoRoot 'src/HASS.Agent.NET10/HASS.Agent.NET10.csproj'
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$') { throw 'Unexpected build version.' }
    $compiler = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$compiler) { throw 'Install Inno Setup 6 before building.' }

    & dotnet test --project tests/HASS.Agent.NET10.Tests/HASS.Agent.NET10.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw '.NET tests failed.' }
    $publish = Join-Path $repoRoot 'artifacts/HASS.Agent.NET10/win-x64'
    & dotnet publish $project -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' "-p:SourceRevisionId=$actualCommit" -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    & $compiler "/DMyAppVersion=$version" (Join-Path $repoRoot 'installer/HASS.Agent.NET10.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

    $installerName = "HASS.Agent.NET10-Setup-$version.exe"
    $installer = Join-Path $repoRoot "artifacts/installer/$installerName"
    $signature = Get-AuthenticodeSignature -LiteralPath $installer
    [ordered]@{
        sourceRepository = 'https://github.com/VorlonCD/HASS.Agent.NET10'
        sourceCommit = $actualCommit
        version = $version
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        runtime = 'win-x64'
        selfContained = $true
        installer = $installerName
        sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
        signatureStatus = [string]$signature.Status
        locallyBuilt = $true
        signedRelease = ($signature.Status -eq 'Valid')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/installer/build-receipt.json') -Encoding utf8
    Write-Output $installer
}
finally { Pop-Location }

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $DotNetPath,
    [string] $IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Build-Release.ps1 requires Windows and an installed Inno Setup 6 compiler.'
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'GlanceBar.csproj'
$publishDirectory = Join-Path $root 'artifacts\publish\win-x64'
$distributionDirectory = Join-Path $root 'dist'
$licenseCache = Join-Path $root 'artifacts\licenses'
$pawnIoVersion = '2.2.0'
$pawnIoHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$pawnIoDirectory = Join-Path $root 'artifacts\tools'
$pawnIoSetup = Join-Path $pawnIoDirectory 'PawnIO_setup.exe'

# Bundle a pinned official installer; never ship an incomplete or substituted download.
New-Item -ItemType Directory -Path $pawnIoDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $pawnIoSetup -PathType Leaf)) {
    $pawnIoUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/$pawnIoVersion/PawnIO_setup.exe"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $pawnIoUrl -OutFile $pawnIoSetup -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $pawnIoSetup -Algorithm SHA256).Hash -ne $pawnIoHash) {
    throw "PawnIO installer checksum does not match $pawnIoVersion. Remove $pawnIoSetup and retry."
}
if ((Get-AuthenticodeSignature -LiteralPath $pawnIoSetup).Status -ne 'Valid') {
    throw 'PawnIO installer signature is not valid.'
}

function Resolve-Tool {
    param([string] $Name, [string] $ExplicitPath, [string[]] $Candidates)
    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "$Name was not found at the explicitly supplied path: $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw "$Name is not installed. Install it first or supply its executable path."
}

$dotnetCandidates = @(
    "$env:ProgramFiles\dotnet\dotnet.exe",
    "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
)
if ($env:DOTNET_ROOT) { $dotnetCandidates = @("$env:DOTNET_ROOT\dotnet.exe") + $dotnetCandidates }
$dotnet = Resolve-Tool 'dotnet.exe' $DotNetPath $dotnetCandidates
$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
foreach ($registryPath in @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
)) {
    $installation = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
    if ($installation -and $installation.PSObject.Properties['InstallLocation']) {
        $isccCandidates += Join-Path $installation.InstallLocation 'ISCC.exe'
    }
}
$iscc = Resolve-Tool 'ISCC.exe' $IsccPath $isccCandidates

[xml] $projectXml = Get-Content -LiteralPath $project -Raw
$versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode -or $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+$') {
    throw 'GlanceBar.csproj must declare a three-component numeric Version.'
}
$version = $versionNode.InnerText
$installerName = "GlanceBar-$version-win-x64-Setup.exe"
$installer = Join-Path $distributionDirectory $installerName
$checksumPath = "$installer.sha256"

# Clear only this script's output, so a failed build can never expose stale release files.
if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $publishDirectory, $distributionDirectory, $licenseCache -Force | Out-Null
foreach ($oldOutput in @($installer, $checksumPath)) {
    if (Test-Path -LiteralPath $oldOutput) { Remove-Item -LiteralPath $oldOutput -Force }
}

Push-Location $root
try {
    & $dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true `
        --output $publishDirectory -p:PublishSingleFile=true -p:PublishTrimmed=false `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
        -p:ContinuousIntegrationBuild=true -p:Deterministic=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'GlanceBar.exe') -PathType Leaf)) {
        throw 'dotnet publish succeeded without producing GlanceBar.exe.'
    }

    # Redistribute the driver's GPL source and its pinned PawnPP submodule with the binary.
    $sourceDirectory = Join-Path $publishDirectory 'licenses'
    New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
    $sources = @(
        @{ Name = 'PawnIO-2.2.0-source.zip'; Repository = 'PawnIO'; Commit = '5cdf470831fdfff3f7f1d06363ca6b230f3bf35a'; Hash = '638D685555DB311EF9F946284DC4CD5720D11C6CB9C5EC5140EBB7F60373440F' },
        @{ Name = 'PawnPP-source.zip'; Repository = 'PawnPP'; Commit = 'e64e4c37b2d8ba0d8ee57205faf8183aee12c438'; Hash = '20AA0638B95C90D296935310F3F28CC22DE1FB24855C5BAA6721E2AE95EB6042' }
    )
    foreach ($source in $sources) {
        $sourcePath = Join-Path $sourceDirectory $source.Name
        Invoke-WebRequest -Uri "https://codeload.github.com/namazso/$($source.Repository)/zip/$($source.Commit)" `
            -OutFile $sourcePath -UseBasicParsing
        if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $source.Hash) {
            throw "Source archive checksum mismatch: $($source.Name)"
        }
    }

    $assetsPath = Join-Path $root 'obj\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    $packages = @{}
    foreach ($libraryProperty in $assets.libraries.PSObject.Properties) {
        $library = $libraryProperty.Value
        if ($library.type -eq 'package') {
            $packages[$libraryProperty.Name] = $library.path
        }
    }

    # Framework/runtime packs are downloadDependencies, not normal libraries in assets.
    # Their LICENSE and THIRD-PARTY-NOTICES are required even for a single-file app.
    $runtimePackFound = $false
    foreach ($frameworkProperty in $assets.project.frameworks.PSObject.Properties) {
        $framework = $frameworkProperty.Value
        if (-not $framework.PSObject.Properties['downloadDependencies']) { continue }
        foreach ($dependency in $framework.downloadDependencies) {
            $dependencyVersion = $dependency.version.Trim([char[]]'[]()').Split(',')[0].Trim()
            if ($dependencyVersion -notmatch '^\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$') {
                throw "Cannot determine exact restored version for $($dependency.name): $($dependency.version)"
            }
            if ($dependency.name -eq 'Microsoft.NETCore.App.Runtime.win-x64') { $runtimePackFound = $true }
            $packages["$($dependency.name)/$dependencyVersion"] = "$($dependency.name.ToLowerInvariant())/$dependencyVersion"
        }
    }
    if (-not $runtimePackFound) {
        throw 'Restored assets do not identify the self-contained win-x64 .NET runtime pack; refusing to omit its license notices.'
    }

    $notices = New-Object System.Text.StringBuilder
    [void] $notices.AppendLine('GlanceBar third-party notices')
    [void] $notices.AppendLine('Dependency versions and copyright/license metadata come from restored NuGet packages.')
    [void] $notices.AppendLine('These notices do not grant or declare a license for GlanceBar itself.')
    [void] $notices.AppendLine('PawnIO 2.2.0: Copyright namazso; GPL-2.0-or-later with the exception in its README.')
    [void] $notices.AppendLine('Source and GPL text: licenses/PawnIO-2.2.0-source.zip; its PawnPP submodule is licenses/PawnPP-source.zip.')
    [void] $notices.AppendLine('')

    foreach ($package in @($packages.Keys | Sort-Object)) {
        $packageDirectory = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path $folder $packages[$package]
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
        }
        if (-not $packageDirectory) { throw "Cannot locate restored package license assets: $package" }
        $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $nuspec) { throw "No NuGet metadata was found for $package." }
        [xml] $metadataXml = Get-Content -LiteralPath $nuspec.FullName -Raw
        $metadata = $metadataXml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
        $licenseUrl = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        [void] $notices.AppendLine(('=' * 72))
        [void] $notices.AppendLine($package)
        if ($copyright) { [void] $notices.AppendLine($copyright.InnerText) }
        if ($license) { [void] $notices.AppendLine("NuGet license ($($license.GetAttribute('type'))): $($license.InnerText)") }
        if ($licenseUrl) { [void] $notices.AppendLine("NuGet license URL: $($licenseUrl.InnerText)") }

        $licenseFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object {
            $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_. ]PARTY[-_. ]NOTICES?)(\..*|[-_].*)?$'
        } | Sort-Object FullName)
        if ($license -and $license.GetAttribute('type') -eq 'file') {
            $declaredLicense = Join-Path $packageDirectory $license.InnerText
            if (-not (Test-Path -LiteralPath $declaredLicense -PathType Leaf)) {
                throw "The package-declared license file is missing for $package."
            }
            $licenseFiles += Get-Item -LiteralPath $declaredLicense
        }
        $licenseFiles = @($licenseFiles | Sort-Object FullName -Unique)
        foreach ($file in $licenseFiles) {
            [void] $notices.AppendLine('')
            [void] $notices.AppendLine('--- ' + $file.FullName.Substring($packageDirectory.Length).TrimStart([char[]]'\/') + ' ---')
            [void] $notices.AppendLine([IO.File]::ReadAllText($file.FullName))
        }
        # This legacy package declares only a license URL, which redirects to Mono's
        # LICENSE on GitHub. Pin that exact file because it is not in the package.
        if ($licenseFiles.Count -eq 0 -and $package -eq 'Mono.Posix.NETStandard/1.0.0' -and
            $licenseUrl -and $licenseUrl.InnerText -eq 'https://go.microsoft.com/fwlink/?linkid=869050') {
            $cachedLicense = Join-Path $licenseCache 'Mono.Posix.NETStandard-1.0.0.txt'
            if (-not (Test-Path -LiteralPath $cachedLicense -PathType Leaf)) {
                $licenseUri = 'https://raw.githubusercontent.com/mono/mono/12dd9040252e5b9c63b8df9ef53777eb01232405/LICENSE'
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $licenseText = [string] (Invoke-WebRequest -Uri $licenseUri -UseBasicParsing).Content
                if ([string]::IsNullOrWhiteSpace($licenseText) -or $licenseText -match '<(!DOCTYPE|html|body)\b') {
                    throw "Cannot retrieve the declared license text for $package."
                }
                [IO.File]::WriteAllText($cachedLicense, $licenseText, (New-Object Text.UTF8Encoding($false)))
            }
            [void] $notices.AppendLine('')
            [void] $notices.AppendLine([IO.File]::ReadAllText($cachedLicense))
            continue
        }
        if ($licenseFiles.Count -eq 0) {
            # NuGet packages may use SPDX metadata without shipping a license file.
            # Retrieve the declared license text, never guess a license from the author.
            if (-not $license -or $license.GetAttribute('type') -ne 'expression' -or
                $license.InnerText -notmatch '^[A-Za-z0-9][A-Za-z0-9.+-]*$') {
                throw "No redistributable license text is available for $package. Inspect the package before releasing."
            }
            $expression = $license.InnerText
            $cachedLicense = Join-Path $licenseCache "$expression.txt"
            if (-not (Test-Path -LiteralPath $cachedLicense -PathType Leaf)) {
                $licenseUri = 'https://licenses.nuget.org/' + [Uri]::EscapeDataString($expression)
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $response = Invoke-WebRequest -Uri $licenseUri -UseBasicParsing
                $licenseText = [string] $response.Content
                if ($licenseText -match '<(!DOCTYPE|html|body)\b') {
                    # licenses.nuget.org returns an HTML reference page. Preserve
                    # only its license-text section, not navigation or site notices.
                    $section = [regex]::Match($licenseText,
                        '(?is)<h2>\s*License text\s*</h2>(.*?)<h2>\s*SPDX web page\s*</h2>')
                    if (-not $section.Success) { throw "Cannot extract NuGet license text for $expression." }
                    $licenseText = [regex]::Replace($section.Groups[1].Value, '(?i)</p\s*>', "`n`n")
                    $licenseText = [regex]::Replace($licenseText, '(?s)<[^>]+>', '')
                    $licenseText = [Net.WebUtility]::HtmlDecode($licenseText).Trim()
                }
                if ([string]::IsNullOrWhiteSpace($licenseText)) {
                    throw "NuGet did not return license content for $expression."
                }
                [IO.File]::WriteAllText($cachedLicense, $licenseText, (New-Object Text.UTF8Encoding($false)))
            }
            [void] $notices.AppendLine('')
            [void] $notices.AppendLine([IO.File]::ReadAllText($cachedLicense))
        }
        if ($package -like 'Microsoft.NETCore.App.Runtime.win-x64/*' -and
            -not ($licenseFiles | Where-Object { $_.Name -match '^THIRD-PARTY-NOTICES' })) {
            throw "The .NET runtime third-party notices were not found in $package."
        }
        [void] $notices.AppendLine('')
    }
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(),
        (New-Object Text.UTF8Encoding($false)))
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $publishDirectory 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $publishDirectory 'LICENSE')

    & $iscc "/DAppVersion=$version" "/DPublishDir=$publishDirectory" `
        "/DInstallerOutputDir=$distributionDirectory" "/DPawnIoSetup=$pawnIoSetup" `
        (Join-Path $root 'packaging\GlanceBar.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "Inno Setup did not produce the expected installer: $installer"
    }
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksumPath, "$hash  $installerName`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "Installer: $installer"
    Write-Host "SHA256:    $hash"
    Write-Host "Checksum:  $checksumPath"
}
finally {
    Pop-Location
}

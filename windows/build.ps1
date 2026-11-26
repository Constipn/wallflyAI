<#
.SYNOPSIS
    Baut Wallfly fuer Windows: optional nativer Audiokern, .NET-Loesung, Tests, Veroeffentlichung und Signatur-Vorschau.
.PARAMETER Native
    Baut zuerst windows\native (CMake-Preset llvm-mingw-release mit -DWF_WITH_AEC3=ON), sofern .tools\llvm-mingw existiert.
.PARAMETER Test
    Fuehrt nach dem Build dotnet test fuer die ganze Loesung aus und danach, sofern native\build\release existiert,
    ctest --preset llvm-mingw-release fuer den nativen Kern. Beide Zusammenfassungen werden ausgegeben; schlaegt eine
    Seite fehl, bricht das Skript danach ab. Live-Tests laufen nur mit WALLFLY_LIVE=1 (README, "Pruefungen").
.PARAMETER Models
    Laedt vor allem anderen per tools\fetch-models.ps1 whisper-server, die Whisper-/VAD-Modelle und die
    Diarisierungsmodelle (SHA-256-geprueft, bereits vorhandene werden nicht neu geladen), damit -Publish sie mitliefert.
.PARAMETER Publish
    Veroeffentlicht Wallfly.exe, "Wallfly Detector.exe" und wallfly-mcp.exe self-contained (win-x64) in EINEN Ordner
    dist\Wallfly (eine gemeinsame Runtime-Kopie), prueft die Assembly-Versionen auf Konflikte und legt WallflyAudio.dll,
    whisper\, models\, third-party\ und VERSION.txt dazu.
.PARAMETER Sign
    Platzhalter fuer die Authenticode-Signatur: zeigt nur, welche Dateien in dist\Wallfly signiert wuerden.
.PARAMETER Partial
    Nur mit -Publish, fuer parallele Entwicklung: ein fehlschlagender Solution-Build bricht nicht ab, Programme, die
    nicht bauen, werden mit Warnung ausgelassen und in VERSION.txt genannt. Die vorige Veroeffentlichung bleibt als
    dist\Wallfly.previous erhalten. Kein Release-Stand.
#>
param([switch]$Test, [switch]$Publish, [switch]$Native, [switch]$Sign, [switch]$Partial, [switch]$Models)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.packages'
$sdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$rid = 'win-x64'
$dist = Join-Path $PSScriptRoot 'dist/Wallfly'

function Assert-ExitCode([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step fehlgeschlagen (Exit-Code $LASTEXITCODE)." }
}

# Version aus Directory.Build.props (<Version>), sonst 0.1.0, plus kurzer Git-Hash.
function Get-WallflyVersion {
    $version = '0.1.0'
    $props = Join-Path $PSScriptRoot 'Directory.Build.props'
    if (Test-Path -LiteralPath $props) {
        $node = ([xml](Get-Content -LiteralPath $props -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
        if ($node -and $node.InnerText.Trim()) { $version = $node.InnerText.Trim() }
    }
    $commit = $null; $modified = $false
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $commit = (& $git.Source -C $PSScriptRoot rev-parse --short HEAD 2>$null)
        if ($LASTEXITCODE -ne 0) { $commit = $null }
        else { $modified = [bool](& $git.Source -C $PSScriptRoot status --porcelain 2>$null | Select-Object -First 1) }
    }
    $full = $version
    if ($commit) { $full = "$version+$commit" }
    [pscustomobject]@{ Version = $version; Commit = $commit; Modified = $modified; Full = $full }
}

# Datei- und Assemblyversion einer DLL/EXE (die Assemblyversion nur bei verwalteten Dateien).
function Get-BinaryVersion([string]$path) {
    $assembly = $null
    try { $assembly = [Reflection.AssemblyName]::GetAssemblyName($path).Version } catch { }
    $info = (Get-Item -LiteralPath $path).VersionInfo
    [pscustomobject]@{
        Assembly = $assembly
        File = New-Object Version ($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
    }
}

# Fuehrt die Veroeffentlichung eines Programms in den gemeinsamen Ordner zusammen. Gleiche Dateien werden
# uebersprungen; bei verschiedenen Fassungen derselben DLL gewinnt die hoehere Version (Assembly-, dann Dateiversion),
# so wie der .NET-Host zwischen NETCore.App und WindowsDesktop.App selbst entscheidet (z. B. WindowsBase.dll: WPF 10.0
# statt der NETCore-Fassade 4.0, die wallfly-mcp sonst ueber die WPF-Fassung schreiben wuerde). Gibt Dateien zurueck,
# die bei gleicher Version verschieden sind; dort bleibt die erste Fassung.
function Merge-Publish([string]$stage, [string]$target) {
    $conflicts = @()
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $relative = $file.FullName.Substring($stage.Length).TrimStart('\', '/')
        $destination = Join-Path $target $relative
        if (-not (Test-Path -LiteralPath $destination)) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            continue
        }
        if ((Get-Item -LiteralPath $destination).Length -eq $file.Length -and
            (Get-FileHash -LiteralPath $destination).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
        if ($file.Extension -notin '.dll', '.exe') { $conflicts += $relative; continue }
        $new = Get-BinaryVersion $file.FullName
        $old = Get-BinaryVersion $destination
        $order = if ($new.Assembly -and $old.Assembly -and $new.Assembly -ne $old.Assembly) { $new.Assembly.CompareTo($old.Assembly) } else { $new.File.CompareTo($old.File) }
        if ($order -gt 0) { Copy-Item -LiteralPath $file.FullName -Destination $destination -Force }
        elseif ($order -eq 0) { $conflicts += $relative }
    }
    return ,$conflicts
}

# Jede <exe>.deps.json muss mit den Dateien des gemeinsamen Ordners laufen: jede erwartete Datei ist da, und ihre
# Assembly- und Dateiversion ist mindestens die erwartete (die Laufzeit bindet nur an gleiche oder hoehere Versionen).
function Test-PublishedAssemblies([string]$folder) {
    $problems = @(); $checked = 0; $versions = @{}
    $depsFiles = @(Get-ChildItem -LiteralPath $folder -Filter '*.deps.json' -File)
    foreach ($deps in $depsFiles) {
        $json = Get-Content -LiteralPath $deps.FullName -Raw | ConvertFrom-Json
        foreach ($target in $json.targets.PSObject.Properties) {
            foreach ($library in $target.Value.PSObject.Properties) {
                foreach ($kind in 'runtime', 'native') {
                    $assets = $library.Value.$kind
                    if (-not $assets) { continue }
                    foreach ($asset in $assets.PSObject.Properties) {
                        $name = ($asset.Name -split '/')[-1]
                        $path = Join-Path $folder $name
                        $checked++
                        if (-not (Test-Path -LiteralPath $path)) { $problems += "$name fehlt ($($deps.Name))"; continue }
                        if (-not $versions.ContainsKey($name)) { $versions[$name] = Get-BinaryVersion $path }
                        $present = $versions[$name]
                        if ($asset.Value.assemblyVersion -and $present.Assembly -and $present.Assembly -lt [version]$asset.Value.assemblyVersion) {
                            $problems += "$name`: Assembly $($present.Assembly) < $($asset.Value.assemblyVersion) ($($deps.Name))"
                        }
                        if ($asset.Value.fileVersion -and $present.File -lt [version]$asset.Value.fileVersion) {
                            $problems += "$name`: Datei $($present.File) < $($asset.Value.fileVersion) ($($deps.Name))"
                        }
                    }
                }
            }
        }
    }
    if ($problems.Count -gt 0) { throw "DLL-Versionskonflikt in dist\Wallfly:`n  " + ($problems -join "`n  ") }
    Write-Host ("Versionspruefung: {0} Eintraege aus {1} deps.json; alle Dateien vorhanden, keine aelter als erwartet." -f $checked, $depsFiles.Count)
}

# Letzte Zeile der dotnet-test- bzw. ctest-Ausgabe, die nach einer Zusammenfassung aussieht.
function Get-TestSummary([object[]]$lines, [string]$pattern) {
    $match = @($lines | ForEach-Object { "$_" } | Where-Object { $_ -match $pattern }) | Select-Object -Last 1
    if ($match) { return $match.Trim() }
    return $null
}

Push-Location $PSScriptRoot
try {
    if ($Models) {
        # Lokales Whisper und Diarisierung: Downloads aus tools\models.lock.json, jede Datei per SHA-256 geprueft.
        & (Join-Path $PSScriptRoot 'tools/fetch-models.ps1')
    }

    if ($Native) {
        if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.tools/llvm-mingw'))) {
            Write-Host 'Hinweis: .tools\llvm-mingw fehlt (native\bootstrap-toolchain.ps1); nativer Build uebersprungen.'
        } else {
            $cmake = Join-Path $PSScriptRoot '.tools/cmake/bin/cmake.exe'
            if (-not (Test-Path -LiteralPath $cmake)) { throw '.tools\cmake fehlt; zuerst native\bootstrap-toolchain.ps1 ausfuehren.' }
            Push-Location (Join-Path $PSScriptRoot 'native')
            try {
                # Produktivkonfiguration: AEC3 + Rauschunterdrueckung gelinkt (WF_WITH_AEC3 ist gecacht, also explizit).
                & $cmake --preset llvm-mingw-release -DWF_WITH_AEC3=ON
                Assert-ExitCode 'CMake-Konfiguration (native)'
                & $cmake --build --preset llvm-mingw-release
                Assert-ExitCode 'Nativer Build'
            }
            finally { Pop-Location }
        }
    }

    & $sdk build Wallfly.sln -c Release
    $buildFailed = $LASTEXITCODE -ne 0
    if ($buildFailed -and -not ($Partial -and $Publish)) { Assert-ExitCode 'Build' }
    if ($buildFailed) { Write-Warning 'Build der Loesung fehlgeschlagen; -Partial veroeffentlicht nur die Programme, die bauen.' }
    if ($Test -and $buildFailed) { Write-Warning 'Tests uebersprungen, weil die Loesung nicht baut.' }
    elseif ($Test) {
        # .NET zuerst, dann der native Kern (ctest); erst nach beiden wird ein Fehler gemeldet. Waehrenddessen bricht eine
        # stderr-Zeile (fehlschlagender Test bei umgeleiteter Ausgabe) das Skript nicht vor der Zusammenfassung ab.
        $dotnetOutput = @()
        $ctestExit = $null; $ctestOutput = @()
        $ctest = Join-Path $PSScriptRoot '.tools/cmake/bin/ctest.exe'
        $nativeBuild = Join-Path $PSScriptRoot 'native/build/release'
        if ((Test-Path -LiteralPath $nativeBuild) -and -not (Test-Path -LiteralPath $ctest)) { throw '.tools\cmake fehlt (ctest); zuerst native\bootstrap-toolchain.ps1 ausfuehren.' }
        $preference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $sdk test Wallfly.sln -c Release --no-build | Tee-Object -Variable dotnetOutput | Out-Host
            $dotnetExit = $LASTEXITCODE
            if (Test-Path -LiteralPath $nativeBuild) {
                Push-Location (Join-Path $PSScriptRoot 'native')
                try {
                    & $ctest --preset llvm-mingw-release | Tee-Object -Variable ctestOutput | Out-Host
                    $ctestExit = $LASTEXITCODE
                }
                finally { Pop-Location }
            } else {
                Write-Host 'Hinweis: native\build\release fehlt; ctest uebersprungen (-Native baut den nativen Kern).'
            }
        }
        finally { $ErrorActionPreference = $preference }
        $dotnetSummary = Get-TestSummary $dotnetOutput '(Bestanden|Fehler|Passed|Failed)!'
        $ctestSummary = Get-TestSummary $ctestOutput 'tests passed'
        Write-Host ''
        Write-Host 'Testzusammenfassung:'
        Write-Host ('  .NET (dotnet test):   ' + $(if ($dotnetSummary) { $dotnetSummary } else { "Exit-Code $dotnetExit" }))
        Write-Host ('  nativ (ctest):        ' + $(if ($null -eq $ctestExit) { 'uebersprungen' } elseif ($ctestSummary) { $ctestSummary } else { "Exit-Code $ctestExit" }))
        if ($dotnetExit -ne 0) { throw "Tests (.NET) fehlgeschlagen (Exit-Code $dotnetExit)." }
        if ($null -ne $ctestExit -and $ctestExit -ne 0) { throw "Tests (nativ, ctest) fehlgeschlagen (Exit-Code $ctestExit)." }
    }

    if ($Publish) {
        # Sauberer Ordner, damit keine Dateien frueherer Builds mitgeliefert werden. Erst umbenennen: schlaegt das fehl,
        # laeuft noch ein Programm aus dist\Wallfly und nichts wurde geloescht.
        $versionFile = Join-Path $dist 'VERSION.txt'
        $previousIsPartial = (Test-Path -LiteralPath $dist) -and (-not (Test-Path -LiteralPath (Join-Path $dist 'Wallfly.exe')) -or
            ((Test-Path -LiteralPath $versionFile) -and (Select-String -LiteralPath $versionFile -Pattern '^Partial:' -Quiet)))
        if ($Partial -and (Test-Path -LiteralPath $dist) -and -not $previousIsPartial) {
            # Teilstand: den vorigen vollstaendigen Stand aufheben statt loeschen (ein Teilstand wird einfach ersetzt).
            if (Test-Path -LiteralPath "$dist.previous") { Remove-Item -LiteralPath "$dist.previous" -Recurse -Force }
            try { Rename-Item -LiteralPath $dist -NewName 'Wallfly.previous' }
            catch { throw 'dist\Wallfly ist in Benutzung (Wallfly, Wallfly Detector oder wallfly-mcp laeuft noch). Bitte beenden und erneut veroeffentlichen.' }
            Write-Host 'Vorige Veroeffentlichung nach dist\Wallfly.previous verschoben.'
        }
        if (Test-Path -LiteralPath $dist) {
            $old = "$dist.old-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
            try { Rename-Item -LiteralPath $dist -NewName (Split-Path -Leaf $old) }
            catch { throw 'dist\Wallfly ist in Benutzung (Wallfly, Wallfly Detector oder wallfly-mcp laeuft noch). Bitte beenden und erneut veroeffentlichen.' }
            Remove-Item -LiteralPath $old -Recurse -Force
        }

        # Alle drei Programme mit derselben RID, jedes zuerst in einen eigenen Stage-Ordner, dann zusammengefuehrt:
        # eine Runtime-Kopie, jede exe mit eigener deps.json/runtimeconfig.json.
        $projects = @(
            @{ Path = 'Wallfly.App/Wallfly.App.csproj'; Name = 'Wallfly' },
            @{ Path = 'Wallfly.Detector/Wallfly.Detector.csproj'; Name = 'Wallfly Detector' },  # Teams-Erkennung, Hintergrundhelfer
            @{ Path = 'Wallfly.Mcp/Wallfly.Mcp.csproj'; Name = 'wallfly-mcp' }                  # lokaler MCP-Server, nur lesend
        )
        $solution = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Wallfly.sln') -Raw
        $missing = @()
        $stageRoot = "$dist.stage"
        if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
        foreach ($project in $projects) {
            if (-not $solution.Contains($project.Path.Replace('/', '\'))) { Write-Host "Hinweis: $($project.Path) ist nicht in Wallfly.sln; uebersprungen."; continue }
            $stage = Join-Path $stageRoot $project.Name
            & $sdk publish $project.Path -c Release -r $rid --self-contained true -o $stage
            if ($LASTEXITCODE -ne 0 -and $Partial) {
                Write-Warning "$($project.Name) baut nicht und fehlt in dist\Wallfly (-Partial)."
                $missing += $project.Name
                continue
            }
            Assert-ExitCode "Publish ($($project.Name))"
            foreach ($conflict in (Merge-Publish $stage $dist)) { Write-Host "Hinweis: $conflict liegt in gleicher Version, aber anderem Build vor (z. B. ReadyToRun aus dem Runtime-Paket); die erste Fassung bleibt." }
        }
        if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
        if (-not (Get-ChildItem -LiteralPath $dist -Filter '*.deps.json' -File -ErrorAction SilentlyContinue)) { throw 'Kein Programm liess sich veroeffentlichen.' }
        Test-PublishedAssemblies $dist

        # Der native Audiokern wird von windows/native/ gebaut (NativeAudioEngine bindet ihn per P/Invoke).
        $nativeDll = Join-Path $PSScriptRoot 'native/build/release/bin/WallflyAudio.dll'
        if (Test-Path -LiteralPath $nativeDll) {
            Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $dist 'WallflyAudio.dll') -Force
            Write-Host 'WallflyAudio.dll in dist\Wallfly kopiert.'
        } else {
            Write-Host 'Warnung: WallflyAudio.dll nicht gefunden (windows/native bauen, z. B. -Native); die App kann nicht aufnehmen.'
        }

        # Lokales Whisper (whisper-server.exe + DLLs) und Modelle, wie sie tools\fetch-models.ps1 ablegt.
        $whisper = Join-Path $PSScriptRoot 'whisper'
        if (Test-Path -LiteralPath $whisper) {
            $target = Join-Path $dist 'whisper'
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            Copy-Item -Path (Join-Path $whisper '*') -Destination $target -Recurse -Force
            Write-Host 'whisper\ kopiert.'
        }
        $modelsDir = Join-Path $PSScriptRoot 'models'
        if (Test-Path -LiteralPath $modelsDir) {
            foreach ($file in Get-ChildItem -LiteralPath $modelsDir -Recurse -File) {
                # ggml-tiny.bin ist nur fuer den gesperrten Entwicklertest und wird laut NOTICES.md nicht ausgeliefert.
                if ($file.Name -eq 'ggml-tiny.bin') { continue }
                $target = Join-Path (Join-Path $dist 'models') $file.FullName.Substring($modelsDir.Length).TrimStart('\', '/')
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            }
            Write-Host 'models\ kopiert (ohne ggml-tiny.bin).'
        }

        # Lizenzhinweise: eigene Liste plus die Hinweise der mitgelieferten .NET-Runtime-Pakete.
        $thirdParty = Join-Path $dist 'third-party'
        New-Item -ItemType Directory -Force -Path $thirdParty | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party/NOTICES.md') -Destination (Join-Path $thirdParty 'NOTICES.md') -Force
        # WebRTC (AEC3 + Rauschunterdrueckung, in WallflyAudio.dll gelinkt): BSD-Lizenz, Patentzusage und der JsonCpp-
        # Hinweis des Snapshots im Original, unter denselben relativen Pfaden wie in native\vendor\webrtc.
        $webrtc = Join-Path $PSScriptRoot 'native/vendor/webrtc'
        foreach ($notice in 'LICENSE', 'JSONCPP-LICENSE', 'vendor-ns/LICENSE', 'vendor-ns/PATENTS') {
            $source = Join-Path $webrtc $notice
            if (-not (Test-Path -LiteralPath $source)) { throw "Lizenzdatei fehlt: native\vendor\webrtc\$notice" }
            $target = Join-Path (Join-Path $thirdParty 'webrtc') $notice
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        foreach ($pack in 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64') {
            $packRoot = Join-Path $env:NUGET_PACKAGES $pack
            if (-not (Test-Path -LiteralPath $packRoot)) { continue }
            $latest = Get-ChildItem -LiteralPath $packRoot -Directory | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
            foreach ($notice in 'THIRD-PARTY-NOTICES.TXT', 'LICENSE.TXT', 'LICENSE') {
                $source = Join-Path $latest.FullName $notice
                if (Test-Path -LiteralPath $source) {
                    $folder = Join-Path $thirdParty ($pack -replace '\.runtime\.win-x64$', '')
                    New-Item -ItemType Directory -Force -Path $folder | Out-Null
                    Copy-Item -LiteralPath $source -Destination (Join-Path $folder $notice) -Force
                }
            }
        }

        $version = Get-WallflyVersion
        $runtime = (Get-Item -LiteralPath (Join-Path $dist 'System.Private.CoreLib.dll')).VersionInfo.ProductVersion -replace '[-+].*$', ''
        $state = if ($version.Modified) { 'modified' } else { 'clean' }
        $commitText = if ($version.Commit) { $version.Commit } else { 'unknown' }
        @(
            $version.Full,
            "Version: $($version.Version)",
            "Commit: $commitText ($state)",
            "Built: $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm')) UTC",
            "Runtime: .NET $runtime, self-contained, $rid",
            $(if ($missing.Count -gt 0) { "Partial: missing $($missing -join ', ')" })
        ) | Where-Object { $_ } | Set-Content -LiteralPath (Join-Path $dist 'VERSION.txt') -Encoding ASCII

        $files = Get-ChildItem -LiteralPath $dist -Recurse -File
        Write-Host ("dist\Wallfly: {0} Dateien, {1:N1} MB, Version {2}." -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $version.Full)
        if ($missing.Count -gt 0) { Write-Warning ("Teilstand ohne: " + ($missing -join ', ') + '. Kein Release.') }
    }

    if ($Sign) {
        # Platzhalter: Zertifikat und Zeitstempeldienst fehlen noch (installer\README.md). Signiert wird nichts.
        if (-not (Test-Path -LiteralPath $dist)) { Write-Host 'Signatur: dist\Wallfly fehlt; zuerst -Publish.' }
        else {
            $own = Get-ChildItem -LiteralPath $dist -File | Where-Object {
                $_.Name -in @('Wallfly.exe', 'Wallfly Detector.exe', 'wallfly-mcp.exe', 'Wallfly.dll', 'Wallfly Detector.dll', 'wallfly-mcp.dll', 'WallflyAudio.dll') -or $_.Name -like 'Wallfly.*.dll'
            }
            Write-Host 'Signatur (Platzhalter, nichts wird signiert). Geplant: signtool sign /fd SHA256 /tr <RFC-3161-Zeitstempel-URL> /td SHA256 /n "<Zertifikat>" <Datei>'
            foreach ($file in $own) { Write-Host "  wuerde signieren: $($file.Name)" }
            Write-Host '  danach: Setup aus installer\wallfly.iss bauen und ebenfalls signieren.'
        }
    }
}
finally { Pop-Location }

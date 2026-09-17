<#
.SYNOPSIS
    Builds GRAccessTools.Common.dll and every tool under src\Tools into bin\.

.DESCRIPTION
    Uses the .NET Framework 4.8 C# compiler (C# 5) that ships with Windows, so no Visual Studio,
    .NET SDK or project files are needed. Everything is compiled as 32-bit (x86) because GRAccess
    is a 32-bit COM component. bin\ is deleted and rebuilt from scratch on every run.

    A tool is any folder at src\Tools\<Category>\<ToolName>\ that contains .cs files. It builds to
    bin\<ToolName>.exe, so tool names must be unique across categories. A tool that needs other
    assemblies lists their paths in a references.txt file in its folder, one per line (# starts a
    comment; environment variables such as %WINDIR% are expanded).

.PARAMETER Clean
    Delete bin\ and stop. Never touches output\ or config\.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Clean
#>
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$bin       = Join-Path $root 'bin'
$commonSrc = Join-Path $root 'src\Common'
$toolsSrc  = Join-Path $root 'src\Tools'
$commonDll = Join-Path $bin 'GRAccessTools.Common.dll'
$csc       = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$grAccess  = Join-Path ${env:CommonProgramFiles(x86)} 'ArchestrA\ArchestrA.GRAccess.dll'

function Stop-Build([string]$message) {
    Write-Host $message -ForegroundColor Red
    exit 1
}

# Start from an empty bin\ so removed or renamed tools never leave stale exes behind
if (Test-Path $bin) {
    try { Remove-Item $bin -Recurse -Force }
    catch { Stop-Build "Could not clear bin\ ($($_.Exception.Message)). Close any running tool and try again." }
}
if ($Clean) {
    Write-Host 'Removed bin\'
    exit 0
}

foreach ($required in @($csc, $grAccess)) {
    if (-not (Test-Path $required)) { Stop-Build "Required file not found: $required" }
}

# x86 for GRAccess; pdb files so stack traces show line numbers
$cscOptions = @('/nologo', '/optimize+', '/debug:pdbonly', '/platform:x86', "/reference:$grAccess")

New-Item -ItemType Directory -Path $bin | Out-Null

# 1. Common library. Every tool depends on it, so stop if it fails.
Write-Host 'Building GRAccessTools.Common.dll'
& $csc @cscOptions '/target:library' "/out:$commonDll" "/recurse:$commonSrc\*.cs"
if ($LASTEXITCODE -ne 0) { Stop-Build 'GRAccessTools.Common.dll failed to build, so no tools were built.' }

# 2. Tools: every src\Tools\<Category>\<ToolName>\ folder that contains .cs files
$tools = @(
    Get-ChildItem $toolsSrc -Directory |
        ForEach-Object { Get-ChildItem $_.FullName -Directory } |
        Where-Object { Get-ChildItem $_.FullName -Filter *.cs -Recurse }
)

$duplicates = @($tools | Group-Object Name | Where-Object { $_.Count -gt 1 })
if ($duplicates.Count -gt 0) {
    $details = $duplicates | ForEach-Object {
        "  $($_.Name): " + (($_.Group | ForEach-Object { $_.FullName.Substring($root.Length + 1) }) -join ', ')
    }
    Stop-Build ("Tool names must be unique because every tool builds into bin\:`n" + ($details -join "`n"))
}

$built  = @()
$failed = @()
foreach ($tool in $tools) {
    $name = $tool.Name
    $sources = @(Get-ChildItem $tool.FullName -Filter *.cs -Recurse)
    if (-not (Select-String -LiteralPath $sources.FullName -Pattern 'STAThread' -SimpleMatch -Quiet)) {
        Write-Host "Skipping ${name}: no [STAThread] Main found (GRAccess needs an STA thread)" -ForegroundColor Red
        $failed += $name
        continue
    }

    # Extra references from the tool's references.txt
    $extraReferences = @()
    $missingReference = $null
    $referencesFile = Join-Path $tool.FullName 'references.txt'
    if (Test-Path $referencesFile) {
        foreach ($line in Get-Content $referencesFile) {
            $entry = $line.Trim()
            if ($entry.Length -eq 0 -or $entry.StartsWith('#')) { continue }
            $path = [Environment]::ExpandEnvironmentVariables($entry)
            if (-not (Test-Path $path)) { $missingReference = $path; break }
            $extraReferences += "/reference:$path"
        }
    }
    if ($missingReference) {
        Write-Host "Skipping ${name}: reference not found: $missingReference" -ForegroundColor Red
        $failed += $name
        continue
    }

    Write-Host "Building $name.exe"
    & $csc @cscOptions '/target:exe' "/reference:$commonDll" @extraReferences "/out:$(Join-Path $bin "$name.exe")" "/recurse:$($tool.FullName)\*.cs"
    if ($LASTEXITCODE -eq 0) { $built += $name } else { $failed += $name }
}

Write-Host ''
if ($failed.Count -gt 0) {
    Stop-Build ("Built $($built.Count) tool(s). FAILED: " + ($failed -join ', '))
}
Write-Host "Built $($built.Count) tool(s) into bin\" -ForegroundColor Green

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$SandboxRoot = "M:\SBoxProjects\sandbox-plus-plus"
)

$ErrorActionPreference = "Stop"

$source = Join-Path $RepoRoot "editor"
$destination = Join-Path $SandboxRoot "Libraries\arenula_mcp"

if (-not (Test-Path $source)) {
    throw "Canonical editor source not found: $source"
}

New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force

$sourceFiles = Get-ChildItem $source -Recurse -File | ForEach-Object {
    $_.FullName.Substring($source.Length).TrimStart('\','/')
} | Sort-Object
$destFiles = Get-ChildItem $destination -Recurse -File | ForEach-Object {
    $_.FullName.Substring($destination.Length).TrimStart('\','/')
} | Sort-Object

$drift = Compare-Object $sourceFiles $destFiles
if ($drift) {
    $drift | Format-Table -AutoSize | Out-String | Write-Error
    throw "Unexpected deployed-source file drift remains."
}

Write-Host "Deployed canonical editor source to $destination"

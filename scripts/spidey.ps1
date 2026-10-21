param(
    [int]$StartPid,
    [string]$Options64,
    [string]$ReadyFile
)
$ErrorActionPreference = 'Stop'

# Snapshot the ancestor chain right away: the launcher (StartPid) waits for
# ReadyFile so the chain is still intact while we walk it.
$procs = @{}
Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name |
    ForEach-Object { $procs[[int]$_.ProcessId] = $_ }

$roots = 'explorer.exe', 'svchost.exe', 'services.exe', 'wininit.exe', 'winlogon.exe', 'sihost.exe', 'system', 'idle'
$chain = New-Object 'System.Collections.Generic.List[int]'
$p = $StartPid
while ($procs.ContainsKey($p) -and $chain.Count -lt 32) {
    $proc = $procs[$p]
    if ($roots -contains $proc.Name.ToLower()) { break }
    $chain.Add($p)
    $parent = [int]$proc.ParentProcessId
    if ($parent -eq $p) { break }
    $p = $parent
}
if ($ReadyFile) { [IO.File]::WriteAllText($ReadyFile, 'ready') }

try {
    $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Options64)) | ConvertFrom-Json

    # Compile once per source revision and cache the DLL.
    # Plain .NET, not Get-FileHash: a PSModulePath inherited from pwsh 7 can hide that cmdlet.
    $src = Join-Path $PSScriptRoot 'Spidey.cs'
    $sha = [Security.Cryptography.SHA1]::Create()
    $hash = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($src))).Replace('-', '').Substring(0, 12)
    $dll = Join-Path $env:TEMP "claude-spidey-$hash.dll"
    if (-not (Test-Path $dll)) {
        Add-Type -Path $src -OutputAssembly $dll -OutputType Library `
            -ReferencedAssemblies System.Drawing, System.Windows.Forms
    }
    Add-Type -Path $dll

    $options = New-Object ClaudeSpidey.Options
    foreach ($prop in $json.PSObject.Properties) { $options.($prop.Name) = $prop.Value }
    $options.Chain = $chain.ToArray()
    [ClaudeSpidey.Program]::Run($options)
}
catch {
    "$(Get-Date -Format s) $_" | Add-Content (Join-Path $env:TEMP 'claude-spidey.log')
}

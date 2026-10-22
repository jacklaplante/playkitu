$ErrorActionPreference = 'Stop'
$script:Private = Join-Path $PSScriptRoot 'private'

function Invoke-BoundedProcess {
    param([string]$Exe, [string[]]$Arguments, [string]$WorkingDirectory,
          [string]$Log, [int]$TimeoutSeconds = 120, [hashtable]$Environment = @{}, [switch]$MinimalEnvironment)
    $info = [Diagnostics.ProcessStartInfo]::new($Exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $WorkingDirectory
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    if ($MinimalEnvironment) {
        $info.Environment.Clear()
        foreach ($key in @('SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'LOCALAPPDATA', 'APPDATA', 'USERPROFILE', 'COMSPEC', 'NUMBER_OF_PROCESSORS', 'PROCESSOR_ARCHITECTURE')) {
            $value = [Environment]::GetEnvironmentVariable($key)
            if ($value) { $info.Environment[$key] = $value }
        }
    }
    # This offline proof never forwards AWS configuration to its child programs.
    foreach ($key in @($info.Environment.Keys)) {
        if ($key -like 'AWS_*' -or $key -like 'GST_*') { $info.Environment.Remove($key) | Out-Null }
    }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $process.Start() | Out-Null
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $peak = 0L
        $cpu = 0.0
        $modules = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $nextModules = 0.0
        while (!$process.WaitForExit(100)) {
            $process.Refresh()
            try { $peak = [Math]::Max($peak, $process.PeakWorkingSet64); $cpu = $process.TotalProcessorTime.TotalSeconds } catch { }
            if ($watch.Elapsed.TotalSeconds -ge $nextModules) {
                try { foreach ($module in $process.Modules) { $modules.Add($module.FileName) | Out-Null } } catch { }
                $nextModules = $watch.Elapsed.TotalSeconds + 1
            }
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        }
        if (!$process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit()
            $code = 124
        } else { $code = $process.ExitCode }
        $watch.Stop()
        ($stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath $Log
        return [pscustomobject]@{ ExitCode = $code; Seconds = $watch.Elapsed.TotalSeconds; PeakWorkingSetBytes = $peak; CpuSeconds = $cpu; Modules = @($modules); Log = $Log }
    } finally { $process.Dispose() }
}

function Get-NativeToolchain {
    # PATH misses installed VS C++ tools. Ask VS Installer for the actual C++
    # workload, including older VS installations, then scope vcvars to this child.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'VS Installer/vswhere missing' }
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'No installed VS x64 C++ toolchain. No global install is performed.' }
    $vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    $cmake = Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    if (!(Test-Path -LiteralPath $cmake)) { throw 'VS bundled CMake missing' }
    $variables = & $env:ComSpec /d /s /c "call `"$vcvars`" >nul && set"
    if ($LASTEXITCODE) { throw 'vcvars64 failed' }
    $result = @{}
    foreach ($line in $variables) {
        if ($line -match '^(PATH|INCLUDE|LIB|LIBPATH|WindowsSdkDir|WindowsSDKVersion|VCToolsInstallDir)=(.*)$') { $result[$Matches[1]] = $Matches[2] }
    }
    return @{ CMake = $cmake; Environment = $result; VS = $vs }
}

function Get-PrivateRuntimeEnvironment {
    param([string]$Runtime, [string]$Evidence,[Parameter(Mandatory)][ValidatePattern('^[a-z]{2}-[a-z]+-[0-9]$')][ValidateLength(6,31)][string]$Region)
    return @{
        PATH = "$Runtime\bin;$env:SystemRoot\System32;$env:SystemRoot"
        PLAYREEL_AWS_REGION = $Region
        GST_PLUGIN_PATH = ''; GST_PLUGIN_PATH_1_0 = ''
        GST_PLUGIN_SYSTEM_PATH = "$Runtime\plugins"
        GST_PLUGIN_SYSTEM_PATH_1_0 = "$Runtime\plugins"
        GST_PLUGIN_SCANNER = "$Runtime\bin\gst-plugin-scanner.exe"
        GST_PLUGIN_SCANNER_1_0 = "$Runtime\bin\gst-plugin-scanner.exe"
        GST_REGISTRY = "$Evidence\registry.bin"
        GST_REGISTRY_1_0 = "$Evidence\registry.bin"
        GST_DEBUG = '2'; GST_DEBUG_FILE = ''
        GST_PRESET_PATH = ''; GST_PRESET_PATH_1_0 = ''
        GST_TRACERS = ''
    }
}

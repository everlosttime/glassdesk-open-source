[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$RootProcessId,
    [Parameter(Mandatory=$true)][string]$PhaseFile,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [string]$Phase='rapid-interaction',
    [int]$MaximumSeconds=900
)
$ErrorActionPreference='Stop'
# Diagnostic only: EX2 is available on patched Win11 22H2 and later. The product
# does not depend on it; unsupported hosts keep using the CIM acceptance sampler.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GlassDeskAnimationMemory {
    [StructLayout(LayoutKind.Sequential)] public struct Counters {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool K32GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);
    public static Counters Read(IntPtr handle) {
        if (!K32GetProcessMemoryInfo(handle, out var counters, (uint)Marshal.SizeOf<Counters>()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return counters;
    }
}
'@
$known=[Collections.Generic.HashSet[int]]::new(); $null=$known.Add($RootProcessId)
$handles=@{}; $creationTimes=@{}; $samples=[Collections.Generic.List[object]]::new(); $watch=[Diagnostics.Stopwatch]::StartNew()
$rootProcess=Get-Process -Id $RootProcessId
$began=$false; $refreshAt=-1.0; $unavailable=$null; $lastSample=$null; $maxGap=0.0
try {
    while ($watch.Elapsed.TotalSeconds -lt $MaximumSeconds) {
        if ($rootProcess.HasExited) { break }
        $name=if(Test-Path -LiteralPath $PhaseFile){[IO.File]::ReadAllText($PhaseFile).Trim()}else{''}
        if ($name -ne $Phase) { if($began){break}; Start-Sleep -Milliseconds 100; continue }
        $began=$true
        if ($watch.Elapsed.TotalSeconds -ge $refreshAt) {
            $processes=@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,CommandLine,CreationDate)
            foreach ($item in $processes) {
                $id=[int]$item.ProcessId
                if($known.Contains($id) -and $creationTimes.ContainsKey($id) -and $creationTimes[$id] -ne $item.CreationDate) {
                    $null=$known.Remove($id); $creationTimes.Remove($id)
                    if($handles.ContainsKey($id)){[GlassDeskAnimationMemory]::CloseHandle($handles[$id])|Out-Null;$handles.Remove($id)}
                }
            }
            do {
                $added=$false
                foreach ($item in $processes) {
                    if ($known.Contains([int]$item.ParentProcessId) -and $item.CommandLine -notlike '*--fullscreen-fixture*') {
                        if($known.Add([int]$item.ProcessId)){$added=$true}
                    }
                }
            } while($added)
            foreach($item in $processes) {
                $id=[int]$item.ProcessId
                if($known.Contains($id) -and -not $handles.ContainsKey($id)) {
                    $creationTimes[$id]=$item.CreationDate
                    $handle=[GlassDeskAnimationMemory]::OpenProcess(0x1000,$false,$id)
                    if($handle -ne [IntPtr]::Zero){$handles[$id]=$handle}
                }
            }
            $refreshAt=$watch.Elapsed.TotalSeconds+2
        }
        [long]$private=0; [long]$commit=0; [long]$working=0; $count=0
        foreach ($id in @($handles.Keys)) {
            try {
                $memory=[GlassDeskAnimationMemory]::Read($handles[$id])
                $private+=$memory.PrivateWorkingSetSize.ToUInt64();$commit+=$memory.PrivateUsage.ToUInt64()
                $working+=$memory.WorkingSetSize.ToUInt64();$count++
            } catch {
                # Closed process handles never refer to a new process after PID reuse.
                [GlassDeskAnimationMemory]::CloseHandle($handles[$id]) | Out-Null; $handles.Remove($id)
                if(-not(Get-Process -Id $id -ErrorAction SilentlyContinue)) {continue}
                $unavailable=$_.Exception.Message
            }
        }
        if($unavailable){break}
        $at=$watch.Elapsed.TotalSeconds
        if($null -ne $lastSample){$maxGap=[Math]::Max($maxGap,$at-$lastSample)}
        $lastSample=$at
        $samples.Add([pscustomobject]@{timestamp=[DateTimeOffset]::Now.ToString('O');elapsedSeconds=$at;phase=$name;
            privateWorkingBytes=$private;privateCommitBytes=$commit;workingBytes=$working;processCount=$count})
        Start-Sleep -Milliseconds 50
    }
} finally {
    $rootProcess.Dispose()
    foreach($handle in $handles.Values){[GlassDeskAnimationMemory]::CloseHandle($handle)|Out-Null}
    [ordered]@{rootProcessId=$RootProcessId;phase=$Phase;source='K32GetProcessMemoryInfo / PROCESS_MEMORY_COUNTERS_EX2';
        reference='https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2';
        intervalTargetMilliseconds=50;maximumObservedGapSeconds=$maxGap;unavailable=$unavailable;
        limitation='Observed sample peak, not an absolute instantaneous maximum. Process discovery occurs every two seconds; console hosts are included. Unsupported systems use the existing CIM sampler.';
        samples=$samples.ToArray()} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $OutputPath -Encoding utf8
}

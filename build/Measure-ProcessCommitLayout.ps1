# Read-only virtual address inventory. Region type/protection is not an allocator
# attribution, and mapped/GPU/image/private figures must not be summed as RAM use.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
if([IntPtr]::Size -ne 8){throw 'Run in a 64-bit PowerShell process.'}
$output=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output file.'}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class GlassDeskCommitInventory {
    [StructLayout(LayoutKind.Sequential)] struct Region {
        public ulong BaseAddress,AllocationBase;
        public uint AllocationProtect,PartitionAndPadding;
        public ulong RegionSize;
        public uint State,Protect,Type;
    }
    public sealed class Item {
        public ulong Address,AllocationBase,Bytes;
        public uint Protection,AllocationProtection,Type;
        public string MappedFile;
    }
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int id);
    [DllImport("kernel32.dll",SetLastError=true)] static extern nuint VirtualQueryEx(SafeProcessHandle process,nint address,out Region region,nuint length);
    [DllImport("psapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetMappedFileName(SafeProcessHandle process,nint address,StringBuilder file,uint length);
    public static List<Item> Read(int id) {
        if(Marshal.SizeOf<Region>()!=48)throw new InvalidOperationException("Unexpected MEMORY_BASIC_INFORMATION layout.");
        using(var process=OpenProcess(0x410,false,id)) {
            if(process.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result=new List<Item>();ulong address=0;
            while(address<0x7fffffff0000UL) {
                var count=VirtualQueryEx(process,(nint)address,out var region,48);
                if(count==0)break;
                if(count!=48 || region.RegionSize==0 || region.BaseAddress+region.RegionSize<=address)
                    throw new InvalidOperationException("Invalid virtual region enumeration.");
                if(region.State==0x1000) {
                    var file=new StringBuilder(1024);
                    if(region.Type!=0x20000)GetMappedFileName(process,(nint)region.BaseAddress,file,1024);
                    result.Add(new Item{Address=region.BaseAddress,AllocationBase=region.AllocationBase,Bytes=region.RegionSize,
                        Protection=region.Protect,AllocationProtection=region.AllocationProtect,Type=region.Type,MappedFile=file.ToString()});
                }
                address=region.BaseAddress+region.RegionSize;
            }
            if(result.Count==0)throw new InvalidOperationException("No committed regions observed.");
            return result;
        }
    }
}
'@
$process=Get-Process -Id $ProcessId
$identity=[ordered]@{id=$process.Id;path=$process.Path;startTime=$process.StartTime;privateBytes=$process.PrivateMemorySize64;workingBytes=$process.WorkingSet64}
$regions=@([GlassDeskCommitInventory]::Read($ProcessId))
$process.Refresh()
if($process.HasExited -or $process.StartTime -ne $identity.startTime){throw 'Process identity changed during observation.'}
$null=New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output)
$groups=@($regions | Group-Object Type | ForEach-Object {
    $kind=switch([uint32]$_.Name){0x20000{'MEM_PRIVATE'};0x40000{'MEM_MAPPED'};0x1000000{'MEM_IMAGE'};default{$_.Name}}
    [pscustomobject]@{type=$kind;committedRegionBytes=($_.Group | Measure-Object Bytes -Sum).Sum;regions=$_.Count}
})
[ordered]@{recordedUtc=[DateTimeOffset]::UtcNow.ToString('O');identity=$identity;committedByRegionType=$groups;regions=$regions;
    forcedGc=$false;memoryMutated=$false;limitation='VirtualQueryEx committed address regions only. MEM_PRIVATE regions are not a GC/native heap attribution. MEM_IMAGE/MEM_MAPPED commitment includes shared mappings; this is not total private commit, resident RAM, or GPU usage.'} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $output -Encoding utf8
$groups

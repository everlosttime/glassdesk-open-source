$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class GlassDeskShaderCompiler {
    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Blob { [PreserveSig] IntPtr GetBufferPointer(); [PreserveSig] UIntPtr GetBufferSize(); }
    [DllImport("d3dcompiler_47.dll", CallingConvention=CallingConvention.StdCall)]
    static extern int D3DCompile(byte[] source, UIntPtr length, string name, IntPtr defines, IntPtr include,
        string entry, string target, uint flags, uint effectFlags, out Blob code, out Blob errors);
    public static void Compile(string sourcePath, string targetPath) {
        byte[] source = File.ReadAllBytes(sourcePath);
        Blob code, errors;
        int result = D3DCompile(source, (UIntPtr)source.Length, sourcePath, IntPtr.Zero, IntPtr.Zero,
            "main", "ps_3_0", 1u << 15, 0, out code, out errors);
        try {
            if (result < 0) throw new InvalidOperationException(errors == null ? result.ToString() :
                Marshal.PtrToStringAnsi(errors.GetBufferPointer()));
            byte[] bytes = new byte[(int)code.GetBufferSize().ToUInt32()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            File.WriteAllBytes(targetPath, bytes);
        } finally {
            if (code != null) Marshal.ReleaseComObject(code);
            if (errors != null) Marshal.ReleaseComObject(errors);
        }
    }
}
'@
$shaderRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/GlassDesk.UI/Effects'
[GlassDeskShaderCompiler]::Compile((Join-Path $shaderRoot 'TransitionGlass.hlsl'), (Join-Path $shaderRoot 'TransitionGlass.ps'))

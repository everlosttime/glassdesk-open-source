using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GlassDesk.Nearby;

namespace GlassDesk.AppServices;

/// <summary>Current-user DPAPI identity; runtime/ is deliberately absent from configuration backups.</summary>
public sealed class NearbyIdentityStore(string directory)
{
    public X509Certificate2 LoadOrCreate()
    {
        var path = Path.Combine(directory, "identity.dpapi");
        Directory.CreateDirectory(directory);
        byte[] pfx;
        if (File.Exists(path)) pfx = Protect(File.ReadAllBytes(path), decrypt: true);
        else
        {
            using var created = CertificateIdentity.Create();
            pfx = created.Export(X509ContentType.Pfx);
            try
            {
                var protectedBytes = Protect(pfx, decrypt: false);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(protectedBytes); output.Flush(true);
            }
            catch { CryptographicOperations.ZeroMemory(pfx); throw; }
        }
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static byte[] Protect(byte[] bytes, bool decrypt)
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var input = new DataBlob { Size = bytes.Length, Data = pinned.AddrOfPinnedObject() };
        DataBlob output = default;
        try
        {
            var ok = decrypt ? CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output)
                : CryptProtectData(ref input, null, 0, 0, 0, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法使用当前 Windows 用户保护附近传输身份。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            pinned.Free();
            if (output.Data != 0)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

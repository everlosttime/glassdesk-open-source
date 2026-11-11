using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

internal static class CaptureCapability
{
    internal static int WriteReport(string path, bool request)
    {
        var size = 0;
        var error = GetCurrentPackageFullName(ref size, null);
        string? package = null;
        if (error == 122)
        {
            var buffer = new StringBuilder(size);
            error = GetCurrentPackageFullName(ref size, buffer);
            if (error == 0) package = buffer.ToString();
        }
        var hasIdentity = error == 0 && package is not null;
        string? permission = null;
        string? failure = null;
        var passed = hasIdentity;
        if (request && hasIdentity)
        {
            try
            {
                var operation = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
                var watch = Stopwatch.StartNew();
                while (!operation.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(45))
                    Program.Pump(TimeSpan.FromMilliseconds(25));
                if (!operation.IsCompleted)
                {
                    passed = false;
                    failure = "System authorization is still pending; do not start borderless capture.";
                }
                else
                {
                    var status = operation.GetAwaiter().GetResult();
                    permission = status.ToString();
                    passed = status == AppCapabilityAccessStatus.Allowed;
                }
            }
            catch (Exception exception) { passed = false; failure = exception.ToString(); }
        }
        else if (request) { passed = false; failure = "Package identity missing; no consent request was attempted."; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed, hasIdentity, package,
            identityError = error, borderlessRequested = request, permission, failure,
            note = "Identity or permission alone does not prove the visual absence of a capture indicator." },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(File.ReadAllText(path));
        return passed ? 0 : 1;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);
}

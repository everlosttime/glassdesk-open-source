using System.Diagnostics;
using System.Reflection;
using System.IO;

internal static class ProbeProcess
{
    internal static Process Start(params string[] arguments)
    {
        var executable=Environment.ProcessPath ?? throw new InvalidOperationException("Probe executable path unavailable.");
        var info=new ProcessStartInfo(executable) {UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,
            RedirectStandardError=true,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach(var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Controlled source process failed to start.");
    }
    internal static string ReadLine(Process process)
    {
        var pending=process.StandardOutput.ReadLineAsync();
        var watch=Stopwatch.StartNew();
        while(!pending.IsCompleted && watch.Elapsed<TimeSpan.FromSeconds(5)) Program.Pump(TimeSpan.FromMilliseconds(10));
        if(!pending.IsCompleted) throw new TimeoutException("Controlled source protocol timed out.");
        return pending.GetAwaiter().GetResult() ?? throw new InvalidOperationException("Controlled source closed its protocol.");
    }
    internal static string Send(Process process,string command)
    { process.StandardInput.WriteLine(command);process.StandardInput.Flush();return ReadLine(process); }
}

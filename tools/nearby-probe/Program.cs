using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Nearby;

if (args.Length < 2) { Console.WriteLine("receive <directory> | send <directory> <IPv4> <port> <files...>"); return; }
await using var service = new NearbyTransferService(new NearbyOptions { LoadIdentity = CertificateIdentity.Create, DefaultReceiveDirectory = Path.GetFullPath(args[1]) });
using var exit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Cancel(); };
var incoming = ""; NearbyTaskState? previousState = null;
service.Changed += (_, _) =>
{
    var state = service.State;
    if (state.Incoming is { } request && request.Id != incoming)
    {
        incoming = request.Id;
        Console.WriteLine($"INCOMING {request.DeviceName} {request.Files.Count}");
        // This isolated probe accepts test fixtures; the product always asks the user.
        _ = service.DecideAsync(request.Id, true);
    }
    var task = args[0] == "send" ? state.Sending : state.Receiving;
    if (task is not null && task.State != previousState)
    {
        previousState = task.State;
        Console.WriteLine($"TASK {task.State} {task.Transferred}/{task.Total} {task.Error}");
        if (args.Contains("--once", StringComparer.Ordinal) && task.State == NearbyTaskState.Completed) exit.CancelAfter(TimeSpan.FromMilliseconds(300));
    }
};
await service.StartAsync(new NearbySettings { DeviceName = "GlassDesk Interop Probe" });
Console.WriteLine($"READY {service.State.Port}");
if (args[0] == "send")
{
    var device = await service.AddDeviceAsync(args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), exit.Token);
    await service.SendAsync(device, FileSelection.Expand(args.Skip(4)).Files, cancellationToken: exit.Token);
    Environment.ExitCode = service.State.Sending?.State == NearbyTaskState.Completed ? 0 : 1;
}
else
{
    try { await Task.Delay(Timeout.Infinite, exit.Token); } catch (OperationCanceledException) { }
}

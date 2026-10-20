using GlassDesk.Platform;
using GlassDesk.Platform.Storage;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "server")
            return await RunServerAsync(args[1], args[2], args[3], args[4]);

        if (args.Length == 3 && args[0] == "send")
        {
            var delivered = await SingleInstanceGuard.TrySendCommandAsync(args[1], args[2], TimeSpan.FromSeconds(3));
            return delivered ? 0 : 3;
        }

        if (args.Length == 5 && args[0] == "userdata-lease")
            return await RunUserDataLeaseAsync(args[1], args[2], args[3], args[4]);

        Console.Error.WriteLine("Usage: server <name> <ready> <message> <stop> | send <name> <command> | userdata-lease <shared|exclusive> <data-root> <ready> <stop>");
        return 64;
    }

    private static async Task<int> RunUserDataLeaseAsync(string mode, string dataRoot, string readyPath, string stopPath)
    {
        var acquired = mode switch
        {
            "shared" => UserDataLease.TryAcquireShared(dataRoot, out var shared) ? shared : null,
            "exclusive" => UserDataLease.TryAcquireExclusive(dataRoot, out var exclusive) ? exclusive : null,
            _ => null,
        };

        if (acquired is null)
        {
            File.WriteAllText(readyPath, "denied");
            return mode is "shared" or "exclusive" ? 3 : 64;
        }

        using (acquired)
        {
            File.WriteAllText(readyPath, "acquired");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!File.Exists(stopPath) && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            return File.Exists(stopPath) ? 0 : 4;
        }
    }

    private static async Task<int> RunServerAsync(string name, string readyPath, string messagePath, string stopPath)
    {
        using var guard = SingleInstanceGuard.Acquire(name);
        if (!guard.IsFirstInstance)
        {
            File.WriteAllText(readyPath, "duplicate");
            return 2;
        }

        guard.CommandReceived += (_, command) => File.WriteAllText(messagePath, command);
        guard.StartListening();
        File.WriteAllText(readyPath, "first");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!File.Exists(stopPath) && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        return File.Exists(stopPath) ? 0 : 4;
    }
}

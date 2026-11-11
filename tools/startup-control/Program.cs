using System.Text.Json;
using GlassDesk.Platform.Startup;

if (args.Length != 2 || args[0] is not ("query" or "enable" or "disable"))
{
    Console.Error.WriteLine("Usage: StartupControl query|enable|disable <full path to GlassDesk.exe>");
    return 2;
}
var service = new StartupRegistration();
if (args[0] == "query")
{
    var status = service.Query(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(status));
    return status.State == StartupRegistrationState.Unavailable ? 1 : 0;
}
var result = service.SetEnabled(args[1], args[0] == "enable");
Console.WriteLine(JsonSerializer.Serialize(result));
return result.Succeeded ? 0 : 1;

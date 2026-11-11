using System;
using System.IO;
using System.Windows.Forms;

namespace NaiwaDesktop;

internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		Native.SetProcessDPIAware();
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(defaultValue: false);

		if (args.Length == 4 && args[0] == "--managed")
		{
			ManagedSession.Run(args[1], args[2], args[3]);
		}
		else if (args.Length == 3 && args[0] == "--self-test")
		{
			Settings.UseManagedState(args[1]);
			Tests.Run(args[1], args[2]);
		}
		else
		{
			Environment.ExitCode = 1;
		}
	}

	internal static void LogError(Exception error)
	{
		try
		{
			Directory.CreateDirectory(Settings.Folder);
			File.AppendAllText(Path.Combine(Settings.Folder, "error.log"),
				DateTime.Now.ToString("s") + " " + error + Environment.NewLine);
		}
		catch (IOException)
		{
			// Error reporting is best effort.
		}
		catch (UnauthorizedAccessException)
		{
			// The state location may be inaccessible during startup failure.
		}
	}
}

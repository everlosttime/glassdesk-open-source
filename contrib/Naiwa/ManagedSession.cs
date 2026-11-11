using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NaiwaDesktop;

internal static class ManagedSession
{
	internal const string WindowTitle = "GlassDesk.Naiwa.Managed";

	internal static void Run(string pipeName, string assetDirectory, string stateDirectory)
	{
		Settings.UseManagedState(stateDirectory);
		try
		{
			Directory.CreateDirectory(Settings.Folder);
			using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			pipe.Connect(5000);
			var utf8 = new UTF8Encoding(false);
			using var reader = new StreamReader(pipe, utf8, false, 1024, leaveOpen: true);
			bool? initial = ReadStartupVisibility(reader);
			if (!initial.HasValue)
			{
				return;
			}

			using var assets = new Assets(assetDirectory);
			using var window = new PetWindow(assets, test: false, managed: true, initiallySleeping: initial.Value);
			byte[] ready = utf8.GetBytes("ready\n");
			pipe.Write(ready, 0, ready.Length);
			bool? current = ReadStartupVisibility(reader);
			if (!current.HasValue)
			{
				return;
			}
			string optionsLine = ReadStartupCommand(reader);
			if (optionsLine == null || optionsLine == "stop")
			{
				return;
			}
			if (!ManagedOptions.TryParse(optionsLine, out var options))
			{
				throw new InvalidDataException("GlassDesk sent invalid pet options.");
			}
			window.ApplyManagedOptions(options);
			window.InitializeManagedView(current.Value);
			var uiStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			ThreadExceptionEventHandler onThreadException = (_, e) =>
			{
				Program.LogError(e.Exception);
				Environment.ExitCode = 1;
				window.Close();
			};
			Application.ThreadException += onThreadException;
			try
			{
				_ = Task.Run(() => ReadCommands(reader, window, uiStopped.Task));
				Application.Run(window);
			}
			finally
			{
				uiStopped.TrySetResult(true);
				Application.ThreadException -= onThreadException;
			}
		}
		catch (Exception ex)
		{
			Program.LogError(ex);
			Environment.ExitCode = 1;
		}
	}

	private static bool? ReadStartupVisibility(StreamReader reader)
	{
		return ReadStartupCommand(reader) switch
		{
			"sleep" => true,
			"wake" => false,
			"stop" or null => null,
			_ => throw new InvalidDataException("GlassDesk sent an invalid startup visibility.")
		};
	}

	private static string ReadStartupCommand(StreamReader reader)
	{
		Task<string> read = reader.ReadLineAsync();
		if (!read.Wait(TimeSpan.FromSeconds(5)))
		{
			throw new TimeoutException("GlassDesk did not send a pet startup command.");
		}
		return read.Result;
	}

	private static async Task ReadCommands(StreamReader reader, PetWindow window, Task uiStopped)
	{
		try
		{
			while (await reader.ReadLineAsync().ConfigureAwait(false) is string command)
			{
				switch (command)
				{
					case "sleep":
						window.BeginInvoke(new Action(() => window.SetManagedSleeping(true)));
						break;
					case "wake":
						window.BeginInvoke(new Action(() => window.SetManagedSleeping(false)));
						break;
					case "hungry":
						window.BeginInvoke(new Action(window.EatDown));
						break;
					case "stop":
						return;
					default:
						if (ManagedOptions.TryParse(command, out var options))
						{
							window.BeginInvoke(new Action(() => window.ApplyManagedOptions(options)));
						}
						break;
				}
			}
		}
		catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
		{
			// The host or the form has disconnected.
		}
		finally
		{
			if (!uiStopped.IsCompleted)
			{
				try
				{
					window.BeginInvoke(new Action(window.Close));
				}
				catch (InvalidOperationException)
				{
					// The form handle may already have been destroyed by the user.
				}
				if (!uiStopped.Wait(TimeSpan.FromSeconds(8)))
				{
					Environment.Exit(2);
				}
			}
		}
	}
}

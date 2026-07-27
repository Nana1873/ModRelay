using System.IO.Pipes;
using ModRelay.Core;

namespace ModRelay.App;

static class Program
{
    private const string MutexName = @"Local\ModRelay.SingleInstance";
    private const string PipeName = "ModRelay.Commands";
    internal const string ShowSettingsCommand = "::show-settings::";
    internal const string TestNotificationCommand = "::test-notification::";

    [STAThread]
    static void Main()
    {
        var files = Environment.GetCommandLineArgs()
            .Skip(1)
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .ToArray();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ForwardToRunningInstance(files);
            return;
        }

        ApplicationConfiguration.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled application error.", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) =>
        {
            Log.Error("Unhandled UI error.", e.Exception);
            MessageBox.Show(e.Exception.Message, AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Application.Run(new TrayApp(PipeName, files));
    }

    private static void ForwardToRunningInstance(IEnumerable<string> files)
    {
        var paths = files.ToArray();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(750);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };

                if (paths.Length == 0)
                    writer.WriteLine(ShowSettingsCommand);
                else
                    foreach (var file in paths)
                        writer.WriteLine(file);

                return;
            }
            catch when (attempt < 2)
            {
                Thread.Sleep(250);
            }
            catch
            {
                return;
            }
        }
    }
}

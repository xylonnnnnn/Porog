using System.Security.Principal;

namespace Porog.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Porog.Desktop." + WindowsIdentity.GetCurrent().User!.Value, out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        var captureIndex = Array.IndexOf(args, "--capture-docs");
        var capturePath = captureIndex >= 0 && args.Length > captureIndex + 1 ? args[captureIndex + 1] : null;
        Application.Run(new MainForm(args.Contains("--tray"), capturePath));
    }
}

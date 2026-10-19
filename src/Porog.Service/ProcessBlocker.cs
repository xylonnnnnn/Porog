using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Porog.Core;

namespace Porog.Service;

public sealed class ProcessBlocker(string ownerSid) : IProcessBlocker
{
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint access, out SafeAccessTokenHandle token);

    public ProcessResult Apply(IReadOnlyCollection<string> executableNames)
    {
        var killed = 0;
        var issues = new List<Issue>();
        foreach (var name in executableNames)
        {
            // Revalidate at the privileged boundary, even though configuration was validated earlier.
            var processName = Path.GetFileNameWithoutExtension(Validation.ProcessName(name));
            foreach (var process in Process.GetProcessesByName(processName))
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    if (!OpenProcessToken(process.Handle, 0x0008, out var token))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    using (token)
                    using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                        if (identity.User?.Value != ownerSid) continue;
                    // Child processes are not killed blindly: they may be unrelated work.
                    process.Kill();
                    if (process.WaitForExit(150)) killed++;
                    else issues.Add(new("PROCESS_CONTROL_FAILED", $"Завершение {name} ещё не подтверждено."));
                }
                catch (InvalidOperationException) { /* Process exited between enumeration and inspection. */ }
                catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or NotSupportedException)
                { issues.Add(new("PROCESS_CONTROL_FAILED", $"Не удалось проверить или завершить {name}.")); }
            }
        }
        return new(killed, issues.Distinct().ToArray());
    }
}

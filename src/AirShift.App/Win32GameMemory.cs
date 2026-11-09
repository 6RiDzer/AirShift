using System.Diagnostics;
using System.Runtime.InteropServices;
using AirShift.Core.Engine;
using AirShift.Core.Game;
using AirShift.Core.Memory;

namespace AirShift.App;

/// <summary>Reads and writes the game's memory from outside the game with ReadProcessMemory.</summary>
internal sealed class Win32GameMemory : IGameMemory
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint StillActive = 259;

    private nint _handle;

    private Win32GameMemory(nint handle, int processId, long moduleBase, long moduleSize, string? fileVersion)
    {
        _handle = handle;
        ProcessId = processId;
        ModuleBase = moduleBase;
        ModuleSize = moduleSize;
        FileVersion = fileVersion;
    }

    public long ModuleBase { get; }
    public long ModuleSize { get; }
    public string? FileVersion { get; }
    public int ProcessId { get; }

    public bool HasExited => _handle == 0 || !GetExitCodeProcess(_handle, out var code) || code != StillActive;

    public static IGameMemory? Open(GameProfile profile, out GameStatus status, out string message)
    {
        foreach (var name in profile.ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length == 0) continue;
            var process = processes[0];
            try
            {
                var handle = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryLimitedInformation | Synchronize, false, process.Id);
                if (handle == 0)
                {
                    status = GameStatus.CannotOpen;
                    message = IsElevated()
                        ? "Windows refused access to the game."
                        : "Could not open the game. Restart AirShift as administrator.";
                    return null;
                }
                ProcessModule? module;
                try
                {
                    module = process.MainModule;
                }
                catch (Exception)
                {
                    module = null;
                }
                if (module is null)
                {
                    CloseHandle(handle);
                    status = GameStatus.CannotOpen;
                    message = "Found the game but could not read it. Restart AirShift as administrator.";
                    return null;
                }
                string? version = null;
                try
                {
                    var info = module.FileVersionInfo;
                    version = info.FileVersion ?? info.ProductVersion;
                }
                catch (Exception)
                {
                }
                status = GameStatus.Attached;
                message = "Attached.";
                return new Win32GameMemory(handle, process.Id, module.BaseAddress, module.ModuleMemorySize, version);
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        status = GameStatus.NotRunning;
        message = "Waiting for " + profile.Title + ".";
        return null;
    }

    public unsafe bool Read(long address, Span<byte> buffer)
    {
        if (_handle == 0) return false;
        fixed (byte* p = buffer)
        {
            return ReadProcessMemory(_handle, (nint)address, (nint)p, buffer.Length, out var read) && read == buffer.Length;
        }
    }

    public unsafe bool Write(long address, ReadOnlySpan<byte> buffer)
    {
        if (_handle == 0) return false;
        fixed (byte* p = buffer)
        {
            return WriteProcessMemory(_handle, (nint)address, (nint)p, buffer.Length, out var written) && written == buffer.Length;
        }
    }

    public void Dispose()
    {
        if (_handle != 0) CloseHandle(_handle);
        _handle = 0;
    }

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, nint buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint address, nint buffer, nint size, out nint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}

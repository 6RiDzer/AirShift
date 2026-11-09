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
    private const int ErrorInsufficientBuffer = 122;

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
    public string? FileVersion { get; private set; }
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
                // The Microsoft Store / Xbox edition carries its own version (3.x) in its package, the one
                // the Store shows; its executable inside says the same as Steam's (6.x). So the package
                // comes first, then the file, then the version resource of the image in memory (the
                // Store edition's folder is protected and Windows may not open the file for us).
                var version = ImageVersion.FromPackagePath(PackageFullName(handle))
                              ?? ImageVersion.FromPackagePath(SafePath(module));
                if (version is null)
                {
                    try
                    {
                        var info = module.FileVersionInfo;
                        version = info.FileVersion ?? info.ProductVersion;
                    }
                    catch (Exception)
                    {
                    }
                }
                status = GameStatus.Attached;
                message = "Attached.";
                var memory = new Win32GameMemory(handle, process.Id, module.BaseAddress, module.ModuleMemorySize, version);
                if (string.IsNullOrWhiteSpace(memory.FileVersion))
                {
                    memory.FileVersion = ImageVersion.FromImage(memory.ReadBytes, memory.ModuleBase, memory.ModuleSize);
                }
                return memory;
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

    /// <summary>The package full name of a Microsoft Store / Xbox game (Publisher.Game_3.483.970.0_x64__id), null for any other.</summary>
    private static string? PackageFullName(nint process)
    {
        try
        {
            uint length = 0;
            if (GetPackageFullName(process, ref length, null) != ErrorInsufficientBuffer || length == 0) return null;
            var name = new char[length];
            return GetPackageFullName(process, ref length, name) == 0 ? new string(name, 0, (int)Math.Max(0, length - 1)) : null;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static string? SafePath(ProcessModule module)
    {
        try
        {
            return module.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private byte[]? ReadBytes(long address, int length)
    {
        var buffer = new byte[length];
        return Read(address, buffer) ? buffer : null;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(nint process, ref uint length, [Out] char[]? fullName);
}

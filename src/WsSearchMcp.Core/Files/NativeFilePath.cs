using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WsSearchMcp.Files;

/// <summary>Resolves the real path an open handle points to (follows symlinks and junctions in any folder).</summary>
public static class NativeFilePath
{
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    /// <summary>
    /// The final path of an open file, e.g. <c>\\?\C:\Users\x\file.txt</c>. Callers pass it through
    /// <see cref="FileAccessPolicy.TryNormalize"/>, which strips the prefix.
    /// </summary>
    public static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        while (true)
        {
            var n = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
            if (n == 0)
                throw new IOException("Could not determine the final path of the open file.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            if (n < buffer.Length) return new string(buffer, 0, (int)n);
            buffer = new char[n + 1]; // the call returned the required size
        }
    }
}

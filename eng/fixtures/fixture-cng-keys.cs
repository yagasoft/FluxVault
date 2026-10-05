#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluxVault.Fixtures;

// Metadata-only enumeration of one current-user software provider. No key export.
public static class FixtureCngKeys
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyName { public IntPtr Name; public IntPtr Algorithm; public uint LegacySpec; public uint Flags; }

    public static string[] CurrentUserNames()
    {
        var status = NCryptOpenStorageProvider(out var provider, "Microsoft Software Key Storage Provider", 0);
        if (status != 0) throw new Win32Exception(status, "The fixture software key provider could not be opened.");
        var state = IntPtr.Zero;
        try
        {
            var names = new List<string>();
            while (true)
            {
                // Current-user scope; refuse providers that require interactive UI.
                status = NCryptEnumKeys(provider, null, out var item, ref state, 0x40);
                if (status == unchecked((int)0x8009002a)) return names.ToArray();
                if (status != 0) throw new Win32Exception(status, "The fixture key baseline could not be enumerated.");
                try
                {
                    var entry = Marshal.PtrToStructure<KeyName>(item);
                    var name = Marshal.PtrToStringUni(entry.Name);
                    if (string.IsNullOrEmpty(name) || name.Length > 1024 || names.Count >= 1024)
                        throw new InvalidOperationException("Fixture key enumeration exceeded its bound.");
                    names.Add(name);
                }
                finally { NCryptFreeBuffer(item); }
            }
        }
        finally
        {
            if (state != IntPtr.Zero) NCryptFreeBuffer(state);
            NCryptFreeObject(provider);
        }
    }

    [DllImport("ncrypt.dll", CharSet=CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptOpenStorageProvider(out IntPtr provider, string name, uint flags);
    [DllImport("ncrypt.dll", CharSet=CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptEnumKeys(IntPtr provider, string? scope, out IntPtr keyName, ref IntPtr state, uint flags);
    [DllImport("ncrypt.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptFreeBuffer(IntPtr buffer);
    [DllImport("ncrypt.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NCryptFreeObject(IntPtr handle);
}

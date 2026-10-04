$ErrorActionPreference = 'Stop'
# Disposable physical NTFS proof. Runs in this finite PowerShell process; no helpers.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FolderRenameProof
{
    [StructLayout(LayoutKind.Sequential)] struct US { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct OA { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr SD, SQOS; }
    [StructLayout(LayoutKind.Sequential)] struct IOS { public IntPtr Status, Information; }
    [DllImport("ntdll.dll")] static extern int NtCreateFile(out SafeFileHandle h, uint access, ref OA oa, out IOS ios, IntPtr size, uint attrs, uint share, uint mode, uint options, IntPtr ea, uint eaSize);
    [DllImport("ntdll.dll")] static extern int NtSetInformationFile(SafeFileHandle h, out IOS ios, IntPtr info, uint size, uint kind);
    [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
    static SafeFileHandle Open(SafeFileHandle parent, string name, uint access, bool dir)
    {
        IntPtr text=Marshal.StringToHGlobalUni(name), str=Marshal.AllocHGlobal(Marshal.SizeOf<US>());
        try {
            Marshal.StructureToPtr(new US { Length=(ushort)(name.Length*2), MaximumLength=(ushort)(name.Length*2), Buffer=text }, str, false);
            var oa=new OA { Length=Marshal.SizeOf<OA>(), Root=parent==null?IntPtr.Zero:parent.DangerousGetHandle(), Name=str, Attributes=0x40 };
            int s=NtCreateFile(out var h, access, ref oa, out _, IntPtr.Zero, 0, 3, 1, dir?0x200021u:0x200060u, IntPtr.Zero, 0);
            if (s!=0) { h.Dispose(); throw new IOException("open: "+RtlNtStatusToDosError(s)); } return h;
        } finally { Marshal.FreeHGlobal(str); Marshal.FreeHGlobal(text); }
    }
    static uint Rename(SafeFileHandle source, SafeFileHandle parent, string name)
    {
        byte[] text=System.Text.Encoding.Unicode.GetBytes(name); int offset=IntPtr.Size==8?20:12;
        IntPtr buffer=Marshal.AllocHGlobal(offset+text.Length);
        try {
            for (int i=0;i<offset;i++) Marshal.WriteByte(buffer,i,0);
            Marshal.WriteIntPtr(buffer, IntPtr.Size==8?8:4, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer,IntPtr.Size==8?16:8,text.Length); Marshal.Copy(text,0,buffer+offset,text.Length);
            int s=NtSetInformationFile(source,out _,buffer,(uint)(offset+text.Length),10); return s==0?0:RtlNtStatusToDosError(s);
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    public static string Run()
    {
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"FluxVault.Tests",Guid.NewGuid().ToString("N"));
        string stage=Path.Combine(root,"stage"), nested=Path.Combine(stage,"nested"), file=Path.Combine(nested,"document.txt"), published=Path.Combine(root,"published");
        Directory.CreateDirectory(nested); File.WriteAllText(file,"verified bytes");
        try {
            uint heldError, closedError;
            using (var parent=Open(null,"\\??\\"+root,0x1000a0,true))
            using (var stageHandle=Open(parent,"stage",0x1100a0,true)) {
                using (var nestedHandle=Open(stageHandle,"nested",0x1000a0,true))
                using (var leaf=Open(nestedHandle,"document.txt",0x120089,false)) {
                    heldError=Rename(stageHandle,parent,"published");
                    using (var stream=new FileStream(leaf,FileAccess.Read)) using (var reader=new StreamReader(stream))
                        if (reader.ReadToEnd()!="verified bytes") throw new IOException("Held bytes changed.");
                }
                closedError=heldError==0?0:Rename(stageHandle,parent,"published");
                if (closedError!=0) throw new IOException("Closed control rename failed: "+closedError);
            }
            if (File.ReadAllText(Path.Combine(published,"nested","document.txt"))!="verified bytes") throw new IOException("Published bytes changed.");
            return System.Text.Json.JsonSerializer.Serialize(new { Root=root, HeldDescendantRenameWin32Error=heldError, ClosedDescendantRenameWin32Error=closedError, SameVolume=true, NestedDirectory=true, ChildDeleteSharing=false, ReplaceExisting=false, PublishedBytesVerified=true });
        } finally {
            string owned=Directory.Exists(published)?published:stage;
            if (File.Exists(Path.Combine(owned,"nested","document.txt"))) File.Delete(Path.Combine(owned,"nested","document.txt"));
            if (Directory.Exists(Path.Combine(owned,"nested"))) Directory.Delete(Path.Combine(owned,"nested"));
            if (Directory.Exists(owned)) Directory.Delete(owned);
            Directory.Delete(root);
        }
    }
}
'@
$result = [FolderRenameProof]::Run() | ConvertFrom-Json
$result | Add-Member -NotePropertyName FixtureRemoved -NotePropertyValue (-not (Test-Path -LiteralPath $result.Root))
$result | Add-Member -NotePropertyName CheckedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O'))
$result | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'native-folder-rename.json')
$result | ConvertTo-Json
if (-not $result.FixtureRemoved) { throw 'Native rename fixture remains.' }

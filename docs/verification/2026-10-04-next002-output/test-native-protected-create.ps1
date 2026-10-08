$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ProtectedCreateProof
{
    [StructLayout(LayoutKind.Sequential)] struct US { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct OA { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr SD, SQOS; }
    [StructLayout(LayoutKind.Sequential)] struct IOS { public IntPtr Status, Information; }
    [DllImport("ntdll.dll")] static extern int NtCreateFile(out SafeFileHandle h, uint access, ref OA oa, out IOS ios, IntPtr size, uint attrs, uint share, uint mode, uint options, IntPtr ea, uint eaSize);
    [DllImport("ntdll.dll")] static extern uint RtlNtStatusToDosError(int status);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, out uint size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
[DllImport("advapi32.dll")] static extern bool GetSecurityDescriptorDacl(IntPtr sd,out bool present,out IntPtr acl,out bool defaulted);
[DllImport("advapi32.dll")] static extern uint SetSecurityInfo(SafeFileHandle handle,uint type,uint info,IntPtr owner,IntPtr group,IntPtr acl,IntPtr sacl);
[DllImport("ntdll.dll")] static extern int NtSetSecurityObject(SafeFileHandle handle,uint info,IntPtr sd);
    static uint Open(SafeFileHandle parent,string name,uint access,uint share,bool directory,bool create,IntPtr sd,out SafeFileHandle handle)
    {
        IntPtr text=Marshal.StringToHGlobalUni(name),str=Marshal.AllocHGlobal(Marshal.SizeOf<US>());
        try {
            Marshal.StructureToPtr(new US { Length=(ushort)(name.Length*2), MaximumLength=(ushort)(name.Length*2), Buffer=text }, str, false);
            var oa=new OA { Length=Marshal.SizeOf<OA>(), Root=parent==null?IntPtr.Zero:parent.DangerousGetHandle(), Name=str, Attributes=0x40, SD=sd };
            int s=NtCreateFile(out handle,access,ref oa,out _,IntPtr.Zero,0,share,create?2u:1u,directory?0x200021u:0x200060u,IntPtr.Zero,0);
            return s==0?0:RtlNtStatusToDosError(s);
        } finally { Marshal.FreeHGlobal(str); Marshal.FreeHGlobal(text); }
    }
    public static string Run()
    {
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"FluxVault.Tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); string sid=WindowsIdentity.GetCurrent().User.Value;
        // Only SYSTEM plus read/execute caller rights. OWNER RIGHTS denies owner DAC/owner changes.
        string sddl="O:"+sid+"D:P(D;OICI;WDWO;;;OW)(A;OICI;FA;;;SY)(A;OICI;FRFX;;;"+sid+")";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl,1,out var sd,out _)) throw new IOException("SD invalid.");
        uint parentError=0,stageError=0,childError=0,fileError=0,writeError=0,winDaclError=0,ntDaclError=0;
        try {
            parentError=Open(null,"\\??\\"+root,0x1000a0,3,true,false,IntPtr.Zero,out var parent);
            using(parent) {
                if(parentError!=0)throw new IOException("Parent failed.");
                stageError=Open(parent,"stage",0x1301a7,0,true,true,sd,out var stage);
                using(stage) {
                    if(stageError==0) {
                        childError=Open(stage,"nested",0x1301a7,0,true,true,sd,out var child);
                        using(child) {
                            {
                                fileError=Open(parent,"document.txt",0x17019f,0,false,true,sd,out var file);
                                using(file) if(fileError==0) {
                                    try { using(var stream=new FileStream(file,FileAccess.ReadWrite)) { stream.Write(new byte[]{1,2,3}); stream.Flush(true); if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;FA;;;"+sid+")(A;;FA;;;SY)",1,out var grant,out _)) throw new IOException("Grant SD invalid."); try { GetSecurityDescriptorDacl(grant,out _,out var dacl,out _); winDaclError=SetSecurityInfo(file,1,0x80000004,IntPtr.Zero,IntPtr.Zero,dacl,IntPtr.Zero); int ns=NtSetSecurityObject(file,4,grant); ntDaclError=ns==0?0:RtlNtStatusToDosError(ns); } finally { LocalFree(grant); } } }
                                    catch(Exception){writeError=1;}
                                }
                            }
                        }
                    }
                }
            }
            return System.Text.Json.JsonSerializer.Serialize(new { Root=root, CallerSid=sid, ParentError=parentError, StageError=stageError, ChildError=childError, FileError=fileError, WriteError=writeError, WinDaclError=winDaclError, NtDaclError=ntDaclError });
        } finally {
            LocalFree(sd);
            // Exact known entries, all beneath our fresh GUID root, without recursive deletion.
            string loose=Path.Combine(root,"document.txt");
            if(File.Exists(loose)) File.Delete(loose);
            if(Directory.Exists(Path.Combine(root,"stage","nested"))) Directory.Delete(Path.Combine(root,"stage","nested"));
            if(Directory.Exists(Path.Combine(root,"stage"))) Directory.Delete(Path.Combine(root,"stage"));
            Directory.Delete(root);
        }
    }
}
'@
$result=[ProtectedCreateProof]::Run() | ConvertFrom-Json
$result | Add-Member FixtureRemoved (-not (Test-Path -LiteralPath $result.Root))
$result | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'native-protected-create.json')
$result | ConvertTo-Json

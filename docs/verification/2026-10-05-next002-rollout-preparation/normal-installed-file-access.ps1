# Read-only S05 proof against installed paths, using the existing restricted-token
# technique. No ACL, account, service, repository or database mutation.
#Requires -Version 7.2
$ErrorActionPreference='Stop'
Add-Type @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
public static class FluxVaultInstalledReadProbe {
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes {public IntPtr Sid; public uint Attributes;}
    [DllImport("advapi32",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32",SetLastError=true)] private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token,uint flags,uint disableCount,ref SidAndAttributes disable,uint privilegeCount,IntPtr privileges,uint restrictCount,IntPtr restrict,out SafeAccessTokenHandle restricted);
    [DllImport("advapi32",SetLastError=true)] private static extern bool DuplicateToken(SafeAccessTokenHandle token,int level,out SafeAccessTokenHandle impersonation);
    [DllImport("advapi32",SetLastError=true)] private static extern bool SetThreadToken(IntPtr thread,SafeAccessTokenHandle token);
    [DllImport("advapi32",SetLastError=true)] private static extern bool RevertToSelf();
    [DllImport("kernel32",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle Open(string path,uint access,uint sharing,IntPtr security,uint disposition,uint flags,IntPtr template);
    private static void Check(bool success,string operation) {if(!success)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),operation);}
    public static Dictionary<string,object> Probe(string control,string state,string marker,string bootstrap) {
        var sid=new SecurityIdentifier("S-1-5-32-544");var bytes=new byte[sid.BinaryLength];sid.GetBinaryForm(bytes,0);
        var memory=Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes,0,memory,bytes.Length);var disable=new SidAndAttributes{Sid=memory};
            using var self=Process.GetCurrentProcess();
            Check(OpenProcessToken(self.Handle,0xE,out var original),"OpenProcessToken");
            using(original) {
                Check(CreateRestrictedToken(original,1,1,ref disable,0,IntPtr.Zero,0,IntPtr.Zero,out var restricted),"CreateRestrictedToken");
                using(restricted) {
                    Check(DuplicateToken(restricted,2,out var impersonation),"DuplicateToken");
                    using(impersonation) {
                        Check(SetThreadToken(IntPtr.Zero,impersonation),"SetThreadToken");
                        try {
                            using var identity=WindowsIdentity.GetCurrent();
                            var admin=new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                            if(admin)throw new InvalidOperationException("Administrator group remains enabled.");
                            var result=new Dictionary<string,object>{{"WindowsSid",identity.User.Value},{"AdministratorEnabled",admin}};
                            foreach(var path in new[]{control,state,marker,bootstrap}) {
                                using var handle=Open(path,1,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
                                result[path]=handle.IsInvalid?Marshal.GetLastWin32Error():0;
                            }
                            return result;
                        } finally {Check(RevertToSelf(),"RevertToSelf");}
                    }
                }
            }
        } finally {Marshal.FreeHGlobal(memory);}
    }
}
'@
$control='C:\Users\os008\FluxVault.LiveValidation.7871ff7f8d1b404db20771f2e742364f\Working files\Notes.txt'
$state='C:\ProgramData\FluxVault\state'
$marker='C:\ProgramData\FluxVault\repository\.fluxvault-storage.json'
$bootstrap='C:\ProgramData\FluxVault\installation.json'
$proof=[FluxVaultInstalledReadProbe]::Probe($control,$state,$marker,$bootstrap)
$proof|ConvertTo-Json -Depth 4
# The bootstrap is inside the same SYSTEM/Administrators-only installation
# root. The desktop client discovers authorised state through IPC, not this file.
if($proof.WindowsSid -cne 'S-1-5-21-136112424-624261118-1239521417-1001' -or $proof[$control] -ne 0 -or $proof[$state] -ne 5 -or $proof[$marker] -ne 5 -or $proof[$bootstrap] -ne 5){throw 'Installed ordinary-user file boundary differs from the contract.'}

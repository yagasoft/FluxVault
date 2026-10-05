using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Commissioning;

// Operator-only correction for the two observed PostgreSQL ancestor descriptors.
// No service, database, file content, owner or child descriptor is changed.
public static class PostgreSqlAncestorAcl
{
    public static bool IsVolumeRoot(string path)
    {
        var canonical=Path.GetFullPath(path);var root=Path.GetPathRoot(canonical);
        if(!string.Equals(canonical,root,StringComparison.OrdinalIgnoreCase))return false;
        var name=new StringBuilder(64);
        if(!GetVolumeNameForVolumeMountPoint(root,name,(uint)name.Capacity))return false;
        using var handle=Open(null,"\\??\\"+root,0x120081);Verify(handle,root);
        return name.ToString().StartsWith("\\\\?\\Volume{",StringComparison.OrdinalIgnoreCase);
    }
    public static void Replace(string[] paths, string[] expected, string[] desired)
    {
        if(paths.Length!=2 || expected.Length!=2 || desired.Length!=2)
            throw new ArgumentException("Exactly two reviewed descriptors are required.");
        if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("An authorised elevated operator is required.");
        var changes=new byte[2][];
        for(var index=0;index<2;index++) {
            var before=new RawSecurityDescriptor(expected[index]);var after=new RawSecurityDescriptor(desired[index]);
            const ControlFlags unsupported=ControlFlags.DiscretionaryAclAutoInherited|ControlFlags.DiscretionaryAclAutoInheritRequired;
            if((before.ControlFlags&unsupported)!=0 || (after.ControlFlags&unsupported)!=0 ||
                !before.Owner.Equals(after.Owner) || !before.Group.Equals(after.Group) || before.SystemAcl!=null || after.SystemAcl!=null)
                throw new ArgumentException("Only the two observed DACL descriptors with unchanged owner/group are supported.");
            changes[index]=new byte[after.BinaryLength];after.GetBinaryForm(changes[index],0);
        }
        var pins=new List<SafeFileHandle>();var targets=new SafeFileHandle[2];
        try {
            for(var index=0;index<2;index++) {
                var path=Path.GetFullPath(paths[index]).TrimEnd('\\');var mount=Path.GetPathRoot(path);
                if(!string.Equals(path,paths[index].TrimEnd('\\'),StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(mount))
                    throw new ArgumentException("Canonical absolute directories are required.");
                var current=Open(null,"\\??\\"+mount,0x120081);pins.Add(current);
                Verify(current,mount);
                var resolved=mount.TrimEnd('\\');var parts=path.Substring(mount.Length).Split('\\');
                for(var part=0;part<parts.Length;part++) {
                    var next=Open(current,parts[part],part==parts.Length-1?0x160081u:0x120081u);
                    pins.Add(next);resolved+="\\"+parts[part];Verify(next,resolved);current=next;
                }
                targets[index]=current;
                var original=Read(current);
                if(!string.Equals(original,expected[index],StringComparison.Ordinal))
                    throw new InvalidOperationException("Ancestor descriptor changed; no ACL is applied. Expected="+expected[index]+" Observed="+original);
            }
            // All acquisition/validation precedes effects. An existing DELETE
            // handle conflicts with these no-DELETE-sharing pins and is a refusal.
            var attempted=-1;
            try {
                for(var index=0;index<2;index++) {
                    attempted=index;SetDescriptor(paths[index],desired[index],changes[index]);
                    var observed=Read(targets[index]);
                    if(!string.Equals(observed,desired[index],StringComparison.Ordinal))
                        throw new InvalidOperationException("Applied descriptor differs; runtime remains blocked. Expected="+desired[index]+" Observed="+observed);
                }
            } catch(Exception failure) {
                var recovery=new List<Exception>{failure};
                for(var index=attempted;index>=0;index--) {
                    try {
                        var sd=new RawSecurityDescriptor(expected[index]);var bytes=new byte[sd.BinaryLength];sd.GetBinaryForm(bytes,0);
                        SetDescriptor(paths[index],expected[index],bytes);
                        if(!string.Equals(Read(targets[index]),expected[index],StringComparison.Ordinal))
                            throw new InvalidOperationException("Original descriptor was not restored exactly; runtime must remain blocked.");
                    } catch(Exception restoreFailure){recovery.Add(restoreFailure);}
                }
                if(recovery.Count>1)throw new AggregateException("Correction failed; preserve the checked recovery inputs and keep runtime blocked.",recovery);
                throw;
            }
        } finally {for(var index=pins.Count-1;index>=0;index--)pins[index].Dispose();}
    }

    private static void SetDescriptor(string path,string sddl,byte[] bytes)
    {
        var sd=new RawSecurityDescriptor(sddl);
        var protection=(sd.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=0?0x80000000u:0x20000000u;
        if(!SetFileSecurity(path,4u|protection,bytes))ThrowLast();
    }

    private static SafeFileHandle Open(SafeFileHandle parent,string name,uint access)
    {
        var text=Marshal.StringToHGlobalUni(name);var unicode=Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try {
            var length=checked((ushort)(name.Length*2));Marshal.StructureToPtr(new UnicodeString{Length=length,MaximumLength=length,Buffer=text},unicode,false);
            var attributes=new ObjectAttributes{Length=Marshal.SizeOf<ObjectAttributes>(),RootDirectory=parent==null?IntPtr.Zero:parent.DangerousGetHandle(),ObjectName=unicode,Attributes=0x1040};
            var status=NtCreateFile(out var handle,access,ref attributes,out _,IntPtr.Zero,0,3,1,0x21,IntPtr.Zero,0);
            if(status!=0){handle.Dispose();throw new Win32Exception((int)RtlNtStatusToDosError(status));}
            return handle;
        } finally {Marshal.FreeHGlobal(unicode);Marshal.FreeHGlobal(text);}
    }

    private static void Verify(SafeFileHandle handle,string path)
    {
        if(!GetFileInformationByHandleEx(handle,9,out var tag,Marshal.SizeOf<AttributeTag>()))ThrowLast();
        if((tag.Attributes&0x10)==0 || (tag.Attributes&0x400)!=0)throw new InvalidOperationException("Ancestor is not a non-reparse directory.");
        var fs=new StringBuilder(64);
        if(!GetVolumeInformationByHandle(handle,IntPtr.Zero,0,out _,out _,out _,fs,(uint)fs.Capacity))ThrowLast();
        if(!string.Equals(fs.ToString(),"NTFS",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Local NTFS is required.");
        var physical=new StringBuilder(32768);var length=GetFinalPathNameByHandle(handle,physical,(uint)physical.Capacity,0);
        if(length==0 || length>=physical.Capacity)ThrowLast();
        if(!string.Equals(physical.ToString().Substring(4).TrimEnd('\\'),path.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Physical ancestor binding changed.");
        var sd=new RawSecurityDescriptor(Read(handle));
        var operatorSid=WindowsIdentity.GetCurrent().User.Value;
        const string trustedInstaller="S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        if(sd.Owner.Value!="S-1-5-18" && sd.Owner.Value!="S-1-5-32-544" && sd.Owner.Value!=operatorSid && sd.Owner.Value!=trustedInstaller)
            throw new InvalidOperationException("Ancestor owner is untrusted: "+path+" ("+sd.Owner.Value+").");
        foreach(GenericAce entry in sd.DiscretionaryAcl) {
            if(!(entry is CommonAce ace)||ace.IsCallback)throw new InvalidOperationException("Ancestor policy is unsupported.");
            if(ace.AceQualifier==AceQualifier.AccessAllowed && (ace.AceFlags&AceFlags.InheritOnly)==0 &&
                ace.SecurityIdentifier.Value!="S-1-5-18" && ace.SecurityIdentifier.Value!="S-1-5-32-544" && ace.SecurityIdentifier.Value!=operatorSid && ace.SecurityIdentifier.Value!=trustedInstaller && (ace.AccessMask&0xC0000)!=0)
                throw new InvalidOperationException("Ancestor permits untrusted security changes.");
        }
    }

    private static string Read(SafeFileHandle handle)
    {
        var error=GetSecurityInfo(handle,1,7,out _,out _,out _,out _,out var descriptor);
        if(error!=0)throw new Win32Exception((int)error);
        try {var bytes=new byte[GetSecurityDescriptorLength(descriptor)];Marshal.Copy(descriptor,bytes,0,bytes.Length);
            return new RawSecurityDescriptor(bytes,0).GetSddlForm(AccessControlSections.All);}
        finally{LocalFree(descriptor);}
    }
    private static void ThrowLast(){throw new Win32Exception(Marshal.GetLastWin32Error());}
    [StructLayout(LayoutKind.Sequential)]private struct UnicodeString{public ushort Length,MaximumLength;public IntPtr Buffer;}
    [StructLayout(LayoutKind.Sequential)]private struct ObjectAttributes{public int Length;public IntPtr RootDirectory,ObjectName;public uint Attributes;public IntPtr SecurityDescriptor,SecurityQualityOfService;}
    [StructLayout(LayoutKind.Sequential)]private struct IoStatus{public IntPtr Status,Information;}
    [StructLayout(LayoutKind.Sequential)]private struct AttributeTag{public uint Attributes,Tag;}
    [DllImport("ntdll")]private static extern int NtCreateFile(out SafeFileHandle file,uint access,ref ObjectAttributes attributes,out IoStatus status,IntPtr allocation,uint fileAttributes,uint sharing,uint disposition,uint options,IntPtr ea,uint eaLength);
    [DllImport("ntdll")]private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32",SetLastError=true)]private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int kind,out AttributeTag value,int size);
    [DllImport("kernel32",EntryPoint="GetVolumeInformationByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetVolumeInformationByHandle(SafeFileHandle file,IntPtr name,uint size,out uint serial,out uint maximum,out uint flags,StringBuilder fs,uint fsSize);
    [DllImport("kernel32",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder name,uint size,uint flags);
    [DllImport("kernel32",EntryPoint="GetVolumeNameForVolumeMountPointW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetVolumeNameForVolumeMountPoint(string root,StringBuilder name,uint size);
    [DllImport("advapi32",EntryPoint="SetFileSecurityW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetFileSecurity(string path,uint information,byte[] descriptor);
    [DllImport("advapi32")]private static extern uint GetSecurityInfo(SafeFileHandle file,int kind,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32")]private static extern IntPtr LocalFree(IntPtr memory);
}

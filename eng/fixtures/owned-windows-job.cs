using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace FluxVault.Fixtures;

public static class WindowsProcessIdentity
{
    public static string ImagePath(System.Diagnostics.Process process)
    {
        // Query the kernel process object, not the transient user-mode module list.
        var length = 32768;
        var path = new StringBuilder(length);
        if (!QueryFullProcessImageName(process.Handle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
}

// Test tooling only. A parent-owned, non-inheritable handle keeps the job alive.
// Every supervisor joins before creating children; closing the last handle kills its entire job.
public sealed class OwnedWindowsJob : IDisposable
{
    private IntPtr handle;
    public string Name { get; }
    private const uint KillOnClose = 0x2000;
    private OwnedWindowsJob(string name, IntPtr handle) { Name = name; this.handle = handle; }

    public static OwnedWindowsJob Create(string name, string actorSid) => Create(name, actorSid, 1);
    public static OwnedWindowsJob CreateSupervisedActor(string name, string actorSid) => Create(name, actorSid, 4);

    private static OwnedWindowsJob Create(string name, string actorSid, uint actorAccess)
    {
        ValidateName(name);
        _ = new SecurityIdentifier(actorSid);
        var owner = WindowsIdentity.GetCurrent().User.Value;
        // Standard-user supervisors need ASSIGN_PROCESS on this object. An elevated creator's
        // implicit high label would deny that access before the explicit actor DACL is checked.
        var sddl = "O:" + owner + "G:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + owner + ")(A;;0x" + actorAccess.ToString("x") + ";;;" + actorSid + ")S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) ThrowLast();
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor, Inherit = false };
            var created = CreateJobObject(ref attributes, name);
            var error = Marshal.GetLastWin32Error();
            if (created == IntPtr.Zero) throw new Win32Exception(error);
            if (error == 183) { CloseHandle(created); throw new InvalidOperationException("Existing job must not be adopted."); }
            var job = new OwnedWindowsJob(name, created);
            try
            {
                var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = KillOnClose } };
                if (!SetInformationJobObject(created, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) ThrowLast();
                job.VerifyOwner(owner);
                return job;
            }
            catch { job.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    public static OwnedWindowsJob Open(string name, string ownerSid)
    {
        ValidateName(name);
        var opened = OpenJobObject(0x20000 | 0x4 | 0x8, false, name); // Read control, query, terminate.
        if (opened == IntPtr.Zero)
        {
            if (Marshal.GetLastWin32Error() == 2) return null;
            ThrowLast();
        }
        var job = new OwnedWindowsJob(name, opened);
        try { job.VerifyOwner(ownerSid); return job; }
        catch { job.Dispose(); throw; }
    }

    public static void JoinCurrent(string name)
    {
        ValidateName(name);
        var opened = OpenJobObject(1, false, name);
        if (opened == IntPtr.Zero) ThrowLast("OpenJobObject"); // Never run child code after a missing/closed parent job.
        try { if (!AssignProcessToJobObject(opened, GetCurrentProcess())) ThrowLast("AssignProcessToJobObject"); }
        finally { CloseHandle(opened); }
    }

    public void Assign(System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) ThrowLast("AssignSupervisedProcessToJobObject");
    }

    public static void WaitForCurrentAdmission(string name)
    {
        ValidateName(name);
        var opened = OpenJobObject(4, false, name);
        if (opened == IntPtr.Zero) ThrowLast("OpenAdmissionJob");
        try
        {
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (!IsProcessInJob(GetCurrentProcess(), opened, out var admitted)) ThrowLast("CheckJobAdmission");
                if (admitted) return;
                Thread.Sleep(25);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException("Supervisor did not admit this actor to its owned job.");
        }
        finally { CloseHandle(opened); }
    }

    public static string CurrentContainment()
    {
        if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) ThrowLast("IsProcessInJob");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var session = process.SessionId;
        var selfAccess = OpenProcess(0x101, false, checked((uint)process.Id));
        var selfAccessError = selfAccess == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        if (selfAccess != IntPtr.Zero) CloseHandle(selfAccess);
        var prefix = "Session=" + session + ";SelfQuotaTerminateError=" + selfAccessError;
        if (!inJob) return prefix + ";InJob=False";
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimits>());
        try
        {
            if (!QueryInformationJobObject(IntPtr.Zero, 9, buffer, Marshal.SizeOf<ExtendedLimits>(), out _))
                return prefix + ";InJob=True;LimitsQueryError=" + Marshal.GetLastWin32Error();
            var flags = Marshal.PtrToStructure<ExtendedLimits>(buffer).Basic.Flags;
            if (!QueryInformationJobObject(IntPtr.Zero, 4, buffer, 4, out _))
                return prefix + ";InJob=True;Limits=" + flags + ";UIQueryError=" + Marshal.GetLastWin32Error();
            return prefix + ";InJob=True;Limits=" + flags + ";UIRestrictions=" + unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public uint[] ProcessIds()
    {
        var buffer = Marshal.AllocHGlobal(8 + 256 * IntPtr.Size);
        try
        {
            if (!QueryInformationJobObject(handle, 3, buffer, 8 + 256 * IntPtr.Size, out _)) ThrowLast();
            var count = Marshal.ReadInt32(buffer, 4);
            if (count < 0 || count > 256) throw new InvalidOperationException("Fixture job process bound exceeded.");
            var ids = new uint[count];
            for (var i = 0; i < count; i++) ids[i] = checked((uint)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64());
            return ids;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void StopAndJoin()
    {
        if (!TerminateJobObject(handle, 1)) ThrowLast();
        for (var i = 0; i < 100; i++) { if (ProcessIds().Length == 0) return; Thread.Sleep(50); }
        throw new InvalidOperationException("Owned job processes remain after termination.");
    }

    private void VerifyOwner(string expected)
    {
        var error = GetSecurityInfo(handle, 6, 1, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try { if (new SecurityIdentifier(owner).Value != expected) throw new InvalidOperationException("Job owner changed; teardown refused."); }
        finally { LocalFree(descriptor); }
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimits>());
        try
        {
            if (!QueryInformationJobObject(handle, 9, buffer, Marshal.SizeOf<ExtendedLimits>(), out _)) ThrowLast();
            if ((Marshal.PtrToStructure<ExtendedLimits>(buffer).Basic.Flags & KillOnClose) == 0) throw new InvalidOperationException("Job lacks process containment.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ValidateName(string name)
    {
        const string prefix = "Global\\FluxVault.NEXT002.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(prefix.Length), "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("A fresh bounded fixture job identity is required.");
    }
    public void Dispose() { var owned = Interlocked.Exchange(ref handle, IntPtr.Zero); if (owned != IntPtr.Zero) CloseHandle(owned); }
    private static void ThrowLast() { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    private static void ThrowLast(string operation) { var error = Marshal.GetLastWin32Error(); throw new Win32Exception(error, operation + " failed with Win32 error " + error + "."); }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessTime, PerJobTime; public uint Flags; public UIntPtr Minimum, Maximum; public uint ActiveLimit; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(ref SecurityAttributes attributes, string name);
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits limits, int size);
    [DllImport("kernel32", SetLastError=true)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, IntPtr result, int size, out int returned);
    [DllImport("kernel32", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32", SetLastError=true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool inJob);
    [DllImport("kernel32", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32", SetLastError=true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("advapi32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint information, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
}

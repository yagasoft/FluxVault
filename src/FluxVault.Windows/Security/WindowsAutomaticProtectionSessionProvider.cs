using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using FluxVault.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace FluxVault.Windows.Security;

/// <summary>LocalSystem acquires only an ordinary token from an actual creator WTS logon.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutomaticProtectionSessionProvider(Action<string>? diagnostic = null) : IAutomaticProtectionSessionProvider
{
    public ValueTask<AutomaticProtectionSession?> AcquireAsync(string creatorSid, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creatorSid);
        foreach (var id in EligibleSessions().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeAccessTokenHandle? token = null;
            try
            {
                token = QueryOrdinaryToken(id);
                var identity = Describe(token);
                if (identity.Sid != creatorSid || identity.SessionId != id) continue;
                var caller = new WindowsFluxVaultCallerContextProvider.OwnedWindowsCallerContext(token);
                token = null;
                try
                {
                    return ValueTask.FromResult<AutomaticProtectionSession?>(new(caller, id, identity.AuthenticationId,
                        ct => IsCurrentAsync(identity, ct)));
                }
                catch { caller.Dispose(); throw; }
            }
            catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
            { diagnostic?.Invoke($"Session {id}: {exception.Message}"); }
            finally { token?.Dispose(); }
        }
        return ValueTask.FromResult<AutomaticProtectionSession?>(null);
    }

    private static ValueTask<bool> IsCurrentAsync(LogonIdentity expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!EligibleSessions().Contains(expected.SessionId)) return ValueTask.FromResult(false);
            using var fresh = QueryOrdinaryToken(expected.SessionId);
            return ValueTask.FromResult(Describe(fresh) == expected);
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
        { return ValueTask.FromResult(false); }
    }

    private static SafeAccessTokenHandle QueryOrdinaryToken(int sessionId)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var original)) NativeFailure();
        using (original)
        {
            SafeAccessTokenHandle? limited = null;
            SafeAccessTokenHandle? ordinary = null;
            try
            {
                var effective = original;
                if (Read<int>(original, 18) == 2) // TokenElevationTypeFull: only substitute its limited linked token.
                {
                    limited = new(Read<IntPtr>(original, 19));
                    if (limited.IsInvalid || Read<int>(limited, 18) != 3)
                        throw new UnauthorizedAccessException("The session has no verified ordinary linked token.");
                    effective = limited;
                }
                var expected = DescribeIdentity(effective);
                // Native LUA filtering only reduces authority. Verified postconditions, rather
                // than requested flags, decide whether a UAC-disabled logon is usable.
                if (!CreateRestrictedToken(effective, 0x1 | 0x4, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out ordinary)) NativeFailure();
                LowerIntegrityToMedium(ordinary);
                VerifyOrdinary(ordinary, effective, expected);
                if (!DuplicateTokenEx(ordinary, 0xE, IntPtr.Zero, 2, 2, out var duplicate)) NativeFailure();
                try { VerifyOrdinary(duplicate, effective, expected); return duplicate; }
                catch { duplicate.Dispose(); throw; }
            }
            finally { ordinary?.Dispose(); limited?.Dispose(); }
        }
    }

    private static LogonIdentity Describe(SafeAccessTokenHandle token)
    {
        if (Read<int>(token, 20) != 0) throw new UnauthorizedAccessException("Elevated automatic source authority is refused.");
        return DescribeIdentity(token);
    }
    private static LogonIdentity DescribeIdentity(SafeAccessTokenHandle token)
    {
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("The session has no user SID.");
        var statistics = Read<TokenStatistics>(token, 10);
        return new(sid, Read<int>(token, 12), $"{statistics.AuthenticationId.High:X8}:{statistics.AuthenticationId.Low:X8}");
    }

    private static void VerifyOrdinary(SafeAccessTokenHandle token, SafeAccessTokenHandle original, LogonIdentity expected)
    {
        if (Describe(token) != expected || Read<int>(token, 26) != 0)
            throw new UnauthorizedAccessException("Ordinary token identity or UI authority could not be verified.");
        if (ReadIntegrity(token) > 8192) throw new UnauthorizedAccessException("Automatic source authority exceeds Medium integrity.");
        if (!LookupPrivilegeValue(null, "SeChangeNotifyPrivilege", out var traverse)) NativeFailure();
        ReadVariable(token, 3, (pointer, size) =>
        {
            var count = Marshal.ReadInt32(pointer);
            if (count is < 0 or > 1 || size != sizeof(int) + count * 12) throw new UnauthorizedAccessException("Automatic source authority retains privileged capabilities.");
            if (count == 1)
            {
                var actual = Marshal.PtrToStructure<Luid>(IntPtr.Add(pointer, sizeof(int)));
                if (actual.Low != traverse.Low || actual.High != traverse.High)
                    throw new UnauthorizedAccessException("Automatic source authority retains a privilege other than directory traversal.");
            }
        });
        using var after = new WindowsIdentity(token.DangerousGetHandle());
        // WindowsIdentity.Groups omits deny-only groups. Inspect native attributes when
        // preserving denial semantics, rather than mistaking filtering for SID removal.
        var groups = ReadGroups(token);
        foreach (var before in ReadGroups(original))
            if ((before.Value & 0x20) == 0 && (!groups.TryGetValue(before.Key, out var attributes) ||
                (before.Value & 0x10) != 0 && (attributes & 0x10) == 0))
                throw new UnauthorizedAccessException("Native filtering removed a deny-bearing source group: " + before.Key);
        var principal = new WindowsPrincipal(after);
        foreach (var group in groups)
        {
            var value = group.Key;
            var privileged = value is "S-1-5-32-544" or "S-1-5-32-547" or "S-1-5-32-548" or
                "S-1-5-32-549" or "S-1-5-32-550" or "S-1-5-32-551" or "S-1-5-114" ||
                value.StartsWith("S-1-5-21-", StringComparison.Ordinal) && (value.EndsWith("-512", StringComparison.Ordinal) ||
                    value.EndsWith("-518", StringComparison.Ordinal) || value.EndsWith("-519", StringComparison.Ordinal));
            if (privileged && ((group.Value & 0x4) != 0 || principal.IsInRole(new SecurityIdentifier(value))))
                throw new UnauthorizedAccessException("An administrative group remains enabled in automatic source authority.");
        }
    }
    private static int ReadIntegrity(SafeAccessTokenHandle token)
    {
        var integrity = -1;
        ReadVariable(token, 25, (pointer, _) =>
        {
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(pointer));
            if (!sid.Value.StartsWith("S-1-16-", StringComparison.Ordinal) || !int.TryParse(sid.Value.AsSpan(7), out integrity))
                throw new UnauthorizedAccessException("The source token integrity label is invalid.");
        });
        return integrity;
    }
    private static void LowerIntegrityToMedium(SafeAccessTokenHandle token)
    {
        if (ReadIntegrity(token) <= 8192) return; // Never raise a lower-integrity source token.
        var sid = new SecurityIdentifier("S-1-16-8192");
        var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        var header = Marshal.SizeOf<SidAndAttributes>();
        var buffer = Marshal.AllocHGlobal(header + bytes.Length);
        try
        {
            var pointer = IntPtr.Add(buffer, header);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.StructureToPtr(new SidAndAttributes { Sid = pointer, Attributes = 0x20 }, buffer, false);
            if (!SetTokenInformation(token, 25, buffer, header + bytes.Length)) NativeFailure();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static Dictionary<string, uint> ReadGroups(SafeAccessTokenHandle token)
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        ReadVariable(token, 2, (pointer, size) =>
        {
            var count = Marshal.ReadInt32(pointer);
            var offset = Marshal.OffsetOf<TokenGroupsHeader>(nameof(TokenGroupsHeader.First)).ToInt32();
            var stride = Marshal.SizeOf<SidAndAttributes>();
            if (count is < 0 or > 65536 || offset + (long)count * stride > size)
                throw new UnauthorizedAccessException("The native source group inventory is invalid.");
            for (var index = 0; index < count; index++)
            {
                var group = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(pointer, offset + index * stride));
                result.Add(new SecurityIdentifier(group.Sid).Value, group.Attributes);
            }
        });
        return result;
    }
    private static void ReadVariable(SafeAccessTokenHandle token, int kind, Action<IntPtr, int> inspect)
    {
        _ = GetTokenInformation(token, kind, IntPtr.Zero, 0, out var size);
        if (size is < 4 or > 1048576) throw new UnauthorizedAccessException("The native token information size is invalid.");
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, kind, pointer, size, out var returned)) NativeFailure();
            if (returned != size) throw new UnauthorizedAccessException("The native token information extent changed.");
            inspect(pointer, size);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static IReadOnlyList<int> EligibleSessions()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var entries, out var count)) NativeFailure();
        try
        {
            if (count is < 0 or > 65536) throw new UnauthorizedAccessException("The native session inventory is invalid.");
            var result = new List<int>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(entries, index * Marshal.SizeOf<SessionInfo>()));
                // These states only select candidates; a fresh verified user token is always required.
                if (item.Id > 0 && item.State is 0 or 1 or 4) result.Add(item.Id);
            }
            return result;
        }
        finally { WTSFreeMemory(entries); }
    }

    private static T Read<T>(SafeAccessTokenHandle token, int kind) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, kind, buffer, size, out var length)) NativeFailure();
            if (length != size) throw new UnauthorizedAccessException("The native token information has an invalid extent.");
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void NativeFailure() => throw new Win32Exception(Marshal.GetLastPInvokeError());
    private sealed record LogonIdentity(string Sid, int SessionId, string AuthenticationId);
    [StructLayout(LayoutKind.Sequential)] private struct SessionInfo { internal int Id; internal IntPtr Name; internal int State; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenGroupsHeader { internal uint Count; internal SidAndAttributes First; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenStatistics
    {
        internal Luid TokenId, AuthenticationId;
        internal long Expiration;
        internal int TokenType, ImpersonationLevel;
        internal uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        internal Luid ModifiedId;
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr entries, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr value, int size, out int length);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing,
        uint flags, uint disableCount, IntPtr disabled, uint deleteCount, IntPtr deleted, uint restrictCount, IntPtr restricted,
        out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr information, int length);
}

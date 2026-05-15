using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FluxVault.App.Services;

public sealed record ExplorerContextMenuStatus(
    bool IsRegistered,
    bool IsClassicRegistered,
    bool IsCompactRegistered,
    string Message);

public interface IExplorerContextMenuService
{
    ExplorerContextMenuStatus GetStatus();

    ExplorerContextMenuStatus Register();

    ExplorerContextMenuStatus Unregister();
}

public interface ICompactExplorerContextMenuRegistration
{
    bool IsRegistered();

    bool TryRegister(out string message);

    bool TryUnregister(out string message);
}

public sealed class WindowsExplorerContextMenuService : IExplorerContextMenuService
{
    private const string CommandSubKeyName = "command";
    private const string FileShellRoot = @"Software\Classes\*\shell";
    private const string DirectoryShellRoot = @"Software\Classes\Directory\shell";

    private static readonly ExplorerContextMenuVerb[] Verbs =
    [
        new("FluxVault01Add", "Add to FluxVault", "--add-path"),
        new("FluxVault02ShowVersions", "Show FluxVault versions", "--show-versions"),
        new("FluxVault03Remove", "Remove from FluxVault", "--remove-path")
    ];

    private readonly string applicationPath;
    private readonly ICompactExplorerContextMenuRegistration compactRegistration;

    public WindowsExplorerContextMenuService(string? applicationPath = null)
        : this(ResolveApplicationPath(applicationPath), new WindowsCompactExplorerContextMenuRegistration(ResolveApplicationPath(applicationPath)))
    {
    }

    internal WindowsExplorerContextMenuService(
        string? applicationPath,
        ICompactExplorerContextMenuRegistration compactRegistration)
    {
        this.applicationPath = ResolveApplicationPath(applicationPath);
        this.compactRegistration = compactRegistration;
    }

    private static string ResolveApplicationPath(string? applicationPath)
    {
        return applicationPath
            ?? Environment.ProcessPath
            ?? Assembly.GetEntryAssembly()?.Location
            ?? Path.Combine(AppContext.BaseDirectory, "FluxVault.App.exe");
    }

    public ExplorerContextMenuStatus GetStatus()
    {
        try
        {
            var classicState = GetClassicRegistrationState();
            var compactRegistered = compactRegistration.IsRegistered();
            return BuildStatus(classicState, compactRegistered);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new ExplorerContextMenuStatus(
                IsRegistered: false,
                IsClassicRegistered: false,
                IsCompactRegistered: false,
                Message: $"Explorer context menu status is unavailable: {ex.Message}");
        }
    }

    public ExplorerContextMenuStatus Register()
    {
        try
        {
            foreach (var root in new[] { FileShellRoot, DirectoryShellRoot })
            {
                foreach (var verb in Verbs)
                {
                    RegisterClassicVerb(root, verb);
                }
            }

            var compactRegistered = compactRegistration.TryRegister(out var compactMessage);
            var status = BuildStatus(GetClassicRegistrationState(), compactRegistered);
            return status with
            {
                Message = compactRegistered
                    ? "Explorer context menu registered for full and compact menus."
                    : $"Full Explorer menu registered. {compactMessage}"
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new ExplorerContextMenuStatus(
                IsRegistered: false,
                IsClassicRegistered: false,
                IsCompactRegistered: false,
                Message: $"Explorer context menu registration failed: {ex.Message}");
        }
    }

    public ExplorerContextMenuStatus Unregister()
    {
        try
        {
            foreach (var root in new[] { FileShellRoot, DirectoryShellRoot })
            {
                foreach (var verb in Verbs)
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{verb.RegistryKey}", throwOnMissingSubKey: false);
                }
            }

            _ = compactRegistration.TryUnregister(out _);
            return new ExplorerContextMenuStatus(
                IsRegistered: false,
                IsClassicRegistered: false,
                IsCompactRegistered: false,
                Message: "Explorer context menu unregistered.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new ExplorerContextMenuStatus(
                IsRegistered: false,
                IsClassicRegistered: false,
                IsCompactRegistered: false,
                Message: $"Explorer context menu removal failed: {ex.Message}");
        }
    }

    private void RegisterClassicVerb(string shellRoot, ExplorerContextMenuVerb verb)
    {
        using var shellKey = Registry.CurrentUser.CreateSubKey($@"{shellRoot}\{verb.RegistryKey}");
        shellKey.SetValue(null, verb.Label);
        shellKey.SetValue("Icon", applicationPath);
        using var commandKey = shellKey.CreateSubKey(CommandSubKeyName);
        commandKey.SetValue(null, BuildCommand(verb));
    }

    private ClassicRegistrationState GetClassicRegistrationState()
    {
        var roots = new[] { FileShellRoot, DirectoryShellRoot };
        var states = roots
            .SelectMany(root => Verbs.Select(verb => GetClassicVerbRegistrationState(root, verb)))
            .ToArray();
        if (states.All(state => state == ClassicRegistrationState.Current))
        {
            return File.Exists(applicationPath)
                ? ClassicRegistrationState.Current
                : ClassicRegistrationState.MissingTarget;
        }

        if (states.Any(state => state == ClassicRegistrationState.Stale))
        {
            return ClassicRegistrationState.Stale;
        }

        return states.Any(state => state == ClassicRegistrationState.Current)
            ? ClassicRegistrationState.Stale
            : ClassicRegistrationState.NotRegistered;
    }

    private ClassicRegistrationState GetClassicVerbRegistrationState(string shellRoot, ExplorerContextMenuVerb verb)
    {
        using var commandKey = Registry.CurrentUser.OpenSubKey($@"{shellRoot}\{verb.RegistryKey}\{CommandSubKeyName}");
        var command = commandKey?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(command))
        {
            return ClassicRegistrationState.NotRegistered;
        }

        return string.Equals(command, BuildCommand(verb), StringComparison.OrdinalIgnoreCase)
            ? ClassicRegistrationState.Current
            : ClassicRegistrationState.Stale;
    }

    private string BuildCommand(ExplorerContextMenuVerb verb)
    {
        return $"\"{applicationPath}\" {verb.ArgumentName} \"%1\"";
    }

    private static ExplorerContextMenuStatus BuildStatus(ClassicRegistrationState classicState, bool compactRegistered)
    {
        var classicRegistered = classicState == ClassicRegistrationState.Current;
        var isRegistered = classicRegistered || compactRegistered;
        var message = (classicState, compactRegistered) switch
        {
            (ClassicRegistrationState.Current, true) => "Explorer context menu is registered for full and Windows 11 compact menus.",
            (ClassicRegistrationState.Current, false) => "Full Explorer menu is registered. Windows 11 compact menu is unavailable without app identity/IExplorerCommand registration.",
            (ClassicRegistrationState.Stale, true) => "Windows 11 compact Explorer menu is registered; full menu has stale FluxVault entries and can be repaired.",
            (ClassicRegistrationState.Stale, false) => "Full Explorer menu has stale FluxVault entries and can be repaired.",
            (ClassicRegistrationState.MissingTarget, true) => "Windows 11 compact Explorer menu is registered; full menu target is missing and can be repaired.",
            (ClassicRegistrationState.MissingTarget, false) => "Full Explorer menu target is missing. Register again after launching the current FluxVault app.",
            (ClassicRegistrationState.NotRegistered, true) => "Windows 11 compact Explorer menu is registered; full menu is not registered.",
            _ => "Explorer context menu is not registered."
        };
        return new ExplorerContextMenuStatus(isRegistered, classicRegistered, compactRegistered, message);
    }

    private enum ClassicRegistrationState
    {
        NotRegistered,
        Current,
        Stale,
        MissingTarget
    }

    private sealed record ExplorerContextMenuVerb(
        string RegistryKey,
        string Label,
        string ArgumentName);
}

internal sealed class WindowsCompactExplorerContextMenuRegistration : ICompactExplorerContextMenuRegistration
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private const string ShellExtensionFileName = "FluxVault.ExplorerCommand.dll";
    private readonly string applicationPath;

    public WindowsCompactExplorerContextMenuRegistration(string? applicationPath = null)
    {
        this.applicationPath = applicationPath
            ?? Environment.ProcessPath
            ?? Assembly.GetEntryAssembly()?.Location
            ?? Path.Combine(AppContext.BaseDirectory, "FluxVault.App.exe");
    }

    public bool IsRegistered()
    {
        return HasPackageIdentity()
            && File.Exists(applicationPath)
            && File.Exists(ResolveShellExtensionPath());
    }

    public bool TryRegister(out string message)
    {
        if (IsRegistered())
        {
            message = "Windows 11 compact menu registration is active through the FluxVault package identity.";
            return true;
        }

        message = !File.Exists(applicationPath)
            ? $"Windows 11 compact menu target is missing: {applicationPath}"
            : HasPackageIdentity()
            ? "Windows 11 compact menu package identity is active, but FluxVault.ExplorerCommand.dll was not found beside the package artefacts."
            : "Windows 11 compact menu registration needs the FluxVault sparse package identity and IExplorerCommand shell extension; full menu registration is active.";
        return false;
    }

    public bool TryUnregister(out string message)
    {
        message = IsRegistered()
            ? "Windows 11 compact menu registration is package-owned; uninstall or unregister the FluxVault sparse package to remove it."
            : "Windows 11 compact menu registration was not active.";
        return false;
    }

    private static string ResolveShellExtensionPath()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var publishRoot = string.Equals(Path.GetFileName(baseDirectory), "app", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(baseDirectory)?.FullName ?? baseDirectory
            : baseDirectory;

        return Path.Combine(publishRoot, "shell-extension", ShellExtensionFileName);
    }

    private static bool HasPackageIdentity()
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            return false;
        }

        if (result != ErrorInsufficientBuffer || length <= 0)
        {
            return false;
        }

        var packageFullName = new char[length];
        result = GetCurrentPackageFullName(ref length, packageFullName);
        return result == 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}

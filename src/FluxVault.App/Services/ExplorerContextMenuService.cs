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
        : this(applicationPath, new WindowsCompactExplorerContextMenuRegistration())
    {
    }

    internal WindowsExplorerContextMenuService(
        string? applicationPath,
        ICompactExplorerContextMenuRegistration compactRegistration)
    {
        this.applicationPath = applicationPath
            ?? Environment.ProcessPath
            ?? Assembly.GetEntryAssembly()?.Location
            ?? Path.Combine(AppContext.BaseDirectory, "FluxVault.App.exe");
        this.compactRegistration = compactRegistration;
    }

    public ExplorerContextMenuStatus GetStatus()
    {
        try
        {
            var classicRegistered = IsClassicRegistered();
            var compactRegistered = compactRegistration.IsRegistered();
            return BuildStatus(classicRegistered, compactRegistered);
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
            var status = BuildStatus(IsClassicRegistered(), compactRegistered);
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

    private bool IsClassicRegistered()
    {
        return new[] { FileShellRoot, DirectoryShellRoot }
            .All(root => Verbs.All(verb => IsClassicVerbRegistered(root, verb)));
    }

    private bool IsClassicVerbRegistered(string shellRoot, ExplorerContextMenuVerb verb)
    {
        using var commandKey = Registry.CurrentUser.OpenSubKey($@"{shellRoot}\{verb.RegistryKey}\{CommandSubKeyName}");
        return string.Equals(
            commandKey?.GetValue(null) as string,
            BuildCommand(verb),
            StringComparison.OrdinalIgnoreCase);
    }

    private string BuildCommand(ExplorerContextMenuVerb verb)
    {
        return $"\"{applicationPath}\" {verb.ArgumentName} \"%1\"";
    }

    private static ExplorerContextMenuStatus BuildStatus(bool classicRegistered, bool compactRegistered)
    {
        var isRegistered = classicRegistered || compactRegistered;
        var message = (classicRegistered, compactRegistered) switch
        {
            (true, true) => "Explorer context menu is registered for full and Windows 11 compact menus.",
            (true, false) => "Full Explorer menu is registered. Windows 11 compact menu is unavailable without app identity/IExplorerCommand registration.",
            (false, true) => "Windows 11 compact Explorer menu is registered; full menu is not registered.",
            _ => "Explorer context menu is not registered."
        };
        return new ExplorerContextMenuStatus(isRegistered, classicRegistered, compactRegistered, message);
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

    public bool IsRegistered()
    {
        return HasPackageIdentity()
            && File.Exists(ResolveShellExtensionPath());
    }

    public bool TryRegister(out string message)
    {
        if (IsRegistered())
        {
            message = "Windows 11 compact menu registration is active through the FluxVault package identity.";
            return true;
        }

        message = HasPackageIdentity()
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

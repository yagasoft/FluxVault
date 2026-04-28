using System.IO;
using System.Reflection;
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

public sealed class WindowsExplorerContextMenuService(string? applicationPath = null) : IExplorerContextMenuService
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

    private readonly string applicationPath = applicationPath
        ?? Environment.ProcessPath
        ?? Assembly.GetEntryAssembly()?.Location
        ?? Path.Combine(AppContext.BaseDirectory, "FluxVault.App.exe");

    public ExplorerContextMenuStatus GetStatus()
    {
        try
        {
            var classicRegistered = IsClassicRegistered();
            var compactRegistered = IsCompactRegistered();
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

            var compactRegistered = TryRegisterCompactMenu(out var compactMessage);
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

            _ = TryUnregisterCompactMenu(out _);
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

    private static bool IsCompactRegistered()
    {
        return false;
    }

    private static bool TryRegisterCompactMenu(out string message)
    {
        // Windows 11 compact menus require an IExplorerCommand implementation registered through
        // package identity/sparse package metadata. FluxVault currently runs as an unpackaged WPF
        // app, so the app exposes a clear unavailable status while still registering the classic
        // full menu. The production installer decision can replace this shim with a packaged COM
        // extension without changing the Options surface.
        message = "Windows 11 compact menu registration needs an app identity and IExplorerCommand shell extension; full menu registration is active.";
        return false;
    }

    private static bool TryUnregisterCompactMenu(out string message)
    {
        message = "Windows 11 compact menu registration was not active.";
        return false;
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

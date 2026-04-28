using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace FluxVault.App.Services;

public sealed record ExplorerContextMenuStatus(bool IsRegistered, string Message);

public interface IExplorerContextMenuService
{
    ExplorerContextMenuStatus GetStatus();

    ExplorerContextMenuStatus Register();

    ExplorerContextMenuStatus Unregister();
}

public sealed class WindowsExplorerContextMenuService(string? applicationPath = null) : IExplorerContextMenuService
{
    private const string FileShellKeyPath = @"Software\Classes\*\shell\FluxVaultRestore";
    private const string DirectoryShellKeyPath = @"Software\Classes\Directory\shell\FluxVaultRestore";
    private const string CommandSubKeyName = "command";

    private readonly string applicationPath = applicationPath
        ?? Environment.ProcessPath
        ?? Assembly.GetEntryAssembly()?.Location
        ?? Path.Combine(AppContext.BaseDirectory, "FluxVault.App.exe");

    public ExplorerContextMenuStatus GetStatus()
    {
        try
        {
            return IsRegistered(FileShellKeyPath) && IsRegistered(DirectoryShellKeyPath)
                ? new ExplorerContextMenuStatus(true, "Explorer context menu is registered for files and folders.")
                : new ExplorerContextMenuStatus(false, "Explorer context menu is not registered.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new ExplorerContextMenuStatus(false, $"Explorer context menu status is unavailable: {ex.Message}");
        }
    }

    public ExplorerContextMenuStatus Register()
    {
        try
        {
            RegisterKey(FileShellKeyPath, "Open FluxVault versions");
            RegisterKey(DirectoryShellKeyPath, "Open FluxVault versions");
            return new ExplorerContextMenuStatus(true, "Explorer context menu registered for files and folders.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new ExplorerContextMenuStatus(false, $"Explorer context menu registration failed: {ex.Message}");
        }
    }

    public ExplorerContextMenuStatus Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(FileShellKeyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(DirectoryShellKeyPath, throwOnMissingSubKey: false);
            return new ExplorerContextMenuStatus(false, "Explorer context menu unregistered.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new ExplorerContextMenuStatus(false, $"Explorer context menu removal failed: {ex.Message}");
        }
    }

    private void RegisterKey(string shellKeyPath, string label)
    {
        using var shellKey = Registry.CurrentUser.CreateSubKey(shellKeyPath);
        shellKey.SetValue(null, label);
        shellKey.SetValue("Icon", applicationPath);
        using var commandKey = shellKey.CreateSubKey(CommandSubKeyName);
        commandKey.SetValue(null, BuildCommand());
    }

    private bool IsRegistered(string shellKeyPath)
    {
        using var commandKey = Registry.CurrentUser.OpenSubKey($@"{shellKeyPath}\{CommandSubKeyName}");
        return string.Equals(
            commandKey?.GetValue(null) as string,
            BuildCommand(),
            StringComparison.OrdinalIgnoreCase);
    }

    private string BuildCommand()
    {
        return $"\"{applicationPath}\" --restore-path \"%1\"";
    }
}

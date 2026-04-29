using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluxVault.Core.Ipc;

namespace FluxVault.Core.Tests;

public sealed class IpcNamedPipeFluxVaultServerSecurityTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Default_pipe_security_allows_desktop_and_packaged_app_clients()
    {
        var security = CreateDefaultPipeSecurity();

        AssertAllows(security, WellKnownSidType.LocalSystemSid, "FullControl");
        AssertAllows(security, WellKnownSidType.BuiltinAdministratorsSid, "FullControl");
        AssertAllows(security, WellKnownSidType.AuthenticatedUserSid, "ReadWrite");
        AssertAllows(security, WellKnownSidType.WinBuiltinAnyPackageSid, "ReadWrite");
    }

    private static object CreateDefaultPipeSecurity()
    {
        var method = typeof(NamedPipeFluxVaultServer).GetMethod(
            "CreateDefaultPipeSecurity",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return method.Invoke(null, null) ?? throw new InvalidOperationException("Pipe security factory returned null.");
    }

    [SupportedOSPlatform("windows")]
    private static void AssertAllows(object security, WellKnownSidType sidType, string expectedRights)
    {
        var sid = new SecurityIdentifier(sidType, null);
        var rules = GetAccessRules(security);

        Assert.Contains(rules, rule =>
        {
            var identity = (IdentityReference)GetProperty(rule, "IdentityReference");
            var accessType = (AccessControlType)GetProperty(rule, "AccessControlType");
            var rights = GetProperty(rule, "PipeAccessRights").ToString() ?? string.Empty;
            return sid.Equals(identity)
                && accessType == AccessControlType.Allow
                && rights.Contains(expectedRights, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IEnumerable<object> GetAccessRules(object security)
    {
        var method = security.GetType().GetMethod(
            "GetAccessRules",
            [typeof(bool), typeof(bool), typeof(Type)]);
        Assert.NotNull(method);

        var rules = method.Invoke(security, [true, false, typeof(SecurityIdentifier)])
            ?? throw new InvalidOperationException("Pipe security returned no access rules.");

        return ((IEnumerable)rules).Cast<object>();
    }

    private static object GetProperty(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name);
        Assert.NotNull(property);
        return property.GetValue(instance)
            ?? throw new InvalidOperationException($"Property {name} returned null.");
    }
}

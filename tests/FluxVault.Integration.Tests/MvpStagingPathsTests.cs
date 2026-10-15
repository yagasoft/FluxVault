using FluxVault.Testing;

namespace FluxVault.Integration.Tests;

public sealed class MvpStagingPathsTests
{
    [Theory]
    [InlineData(@"I:\FluxVault Staging\MVP\00112233445566778899aabbccddeeff")]
    [InlineData(@"i:\FLUXVAULT STAGING\MVP\00112233445566778899AABBCCDDEEFF\")]
    public void Owned_absolute_I_root_is_normalised(string path)
    {
        Assert.Equal(@"I:\FluxVault Staging\MVP\00112233445566778899aabbccddeeff",
            MvpStagingPaths.ResolveRoot(path), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"C:\Users\os008\FluxVault Staging\MVP\00112233445566778899aabbccddeeff")]
    [InlineData(@"I:\FluxVault Staging\Other\00112233445566778899aabbccddeeff")]
    [InlineData(@"I:\FluxVault Staging\MVP\00112233445566778899aabbccddeeff\00112233445566778899aabbccddeeff")]
    [InlineData(@"I:\FluxVault Staging\MVP\00000000000000000000000000000000")]
    [InlineData(@"I:\FluxVault Staging\MVP\not-a-run")]
    [InlineData(@"I:\FluxVault Staging\MVP\00112233445566778899aabbccddeeff:payload")]
    [InlineData(@"I:FluxVault Staging\MVP\00112233445566778899aabbccddeeff")]
    [InlineData(@"00112233445566778899aabbccddeeff")]
    public void Other_roots_are_refused_before_installed_access(string path)
    {
        Assert.Throws<InvalidDataException>(() => MvpStagingPaths.ResolveRoot(path));
    }
}

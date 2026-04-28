using FluxVault.App.Services;

namespace FluxVault.App.Tests;

public sealed class AppStartupRequestRouterTests
{
    [Fact]
    public void Restore_path_argument_parser_accepts_file_or_folder_path()
    {
        var request = AppStartupRequest.Parse(["--restore-path", @"D:\Work\Drafts"]);

        Assert.Equal(AppStartupRequestAction.ShowVersions, request.Action);
        Assert.Equal(@"D:\Work\Drafts", request.RestorePath);
        Assert.Equal(@"D:\Work\Drafts", request.Path);
    }

    [Theory]
    [InlineData("--show-versions", AppStartupRequestAction.ShowVersions)]
    [InlineData("--add-path", AppStartupRequestAction.AddToFluxVault)]
    [InlineData("--remove-path", AppStartupRequestAction.RemoveFromFluxVault)]
    public void Explorer_action_argument_parser_accepts_context_menu_actions(
        string argument,
        AppStartupRequestAction expectedAction)
    {
        var request = AppStartupRequest.Parse([argument, @"D:\Work\Drafts\brief.docx"]);

        Assert.Equal(expectedAction, request.Action);
        Assert.Equal(@"D:\Work\Drafts\brief.docx", request.Path);
    }

    [Fact]
    public async Task Existing_instance_receives_restore_path_request_from_second_launch()
    {
        var instanceKey = $"FluxVault.App.Tests.{Guid.NewGuid():N}";
        await using var primary = new AppStartupRequestRouter(instanceKey);
        var received = new TaskCompletionSource<AppStartupRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await primary.StartListeningAsync(request =>
        {
            received.TrySetResult(request);
            return Task.CompletedTask;
        });
        await using var secondary = new AppStartupRequestRouter(instanceKey);

        var forwarded = await secondary.TryForwardToExistingInstanceAsync(
            new AppStartupRequest(AppStartupRequestAction.ShowVersions, @"D:\Work\Drafts\brief.docx"));

        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);
        Assert.True(forwarded);
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"D:\Work\Drafts\brief.docx", request.RestorePath);
    }
}

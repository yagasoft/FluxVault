using System.IO;
using FluxVault.App.Services;

namespace FluxVault.App.Tests;

public sealed class DataGridLayoutStoreTests
{
    [Fact]
    public void Missing_layout_state_returns_empty_layout()
    {
        using var temp = TempFolder.Create();
        var store = new FileDataGridLayoutStore(Path.Combine(temp.Path, "ui-state.json"));

        var layout = store.Load("mirrors");

        Assert.Empty(layout);
    }

    [Fact]
    public void Saved_layout_state_round_trips_column_widths_and_order()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "ui-state.json");
        var store = new FileDataGridLayoutStore(path);

        store.Save(
            "mirrors",
            [
                new DataGridColumnLayout("Label", 180, 0),
                new DataGridColumnLayout("Path", 420, 1)
            ]);

        var loaded = new FileDataGridLayoutStore(path).Load("mirrors");

        Assert.Equal(2, loaded.Count);
        Assert.Equal(new DataGridColumnLayout("Label", 180, 0), loaded[0]);
        Assert.Equal(new DataGridColumnLayout("Path", 420, 1), loaded[1]);
    }

    [Fact]
    public void Corrupt_layout_state_is_ignored_safely()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "ui-state.json");
        File.WriteAllText(path, "{ this is not json");
        var store = new FileDataGridLayoutStore(path);

        var layout = store.Load("mirrors");

        Assert.Empty(layout);
    }

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempFolder Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"FluxVault.App.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempFolder(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

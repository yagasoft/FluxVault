using System.IO;
using System.Text.Json;

namespace FluxVault.App.Services;

public sealed record DataGridColumnLayout(string Key, double Width, int DisplayIndex);

public interface IDataGridLayoutStore
{
    IReadOnlyList<DataGridColumnLayout> Load(string gridKey);

    void Save(string gridKey, IReadOnlyList<DataGridColumnLayout> columns);
}

public sealed class FileDataGridLayoutStore : IDataGridLayoutStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string path;

    public FileDataGridLayoutStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FluxVault",
            "ui-state.json"))
    {
    }

    public FileDataGridLayoutStore(string path)
    {
        this.path = path;
    }

    public IReadOnlyList<DataGridColumnLayout> Load(string gridKey)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var state = JsonSerializer.Deserialize<DataGridLayoutState>(
                File.ReadAllText(path),
                SerializerOptions);
            return state?.Grids.TryGetValue(gridKey, out var columns) == true
                ? columns
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(string gridKey, IReadOnlyList<DataGridColumnLayout> columns)
    {
        try
        {
            var state = LoadState();
            state.Grids[gridKey] = columns.ToArray();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(state, SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private DataGridLayoutState LoadState()
    {
        try
        {
            if (!File.Exists(path))
            {
                return DataGridLayoutState.Empty();
            }

            return JsonSerializer.Deserialize<DataGridLayoutState>(File.ReadAllText(path), SerializerOptions)
                ?? DataGridLayoutState.Empty();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return DataGridLayoutState.Empty();
        }
    }

    private sealed record DataGridLayoutState(Dictionary<string, DataGridColumnLayout[]> Grids)
    {
        public static DataGridLayoutState Empty()
        {
            return new DataGridLayoutState([]);
        }
    }
}

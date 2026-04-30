using FluxVault.Abstractions.Policies;

namespace FluxVault.Core.Policies;

public sealed record WorkloadPolicyPresetOption(
    WorkloadPolicyPresetId Id,
    string DisplayName,
    string Description);

public sealed record WorkloadPolicyExtensionOverride(
    string Extension,
    CompressionPreference? Compression,
    int? MinimumCompressionBytes);

public sealed record WorkloadPolicyPreset(
    WorkloadPolicyPresetId Id,
    string DisplayName,
    string Description,
    ResourceProfile ResourceProfile,
    CompressionPreference Compression,
    int MinimumCompressionBytes,
    IReadOnlyList<string> ExcludedFolderNames,
    IReadOnlyList<string> ExcludedExtensions,
    IReadOnlyList<string> NoCompressionExtensions,
    IReadOnlyList<WorkloadPolicyExtensionOverride> ExtensionOverrides);

public static class WorkloadPolicyPresetCatalog
{
    private static readonly WorkloadPolicyPreset[] BuiltInPresets =
    [
        new WorkloadPolicyPreset(
            WorkloadPolicyPresetId.GeneralPurpose,
            "General purpose",
            "Balanced protection for normal mixed folders.",
            ResourceProfile.Balanced,
            CompressionPreference.Zstd,
            256 * 1024,
            [],
            [],
            [],
            []),
        new WorkloadPolicyPreset(
            WorkloadPolicyPresetId.OfficeDocuments,
            "Office documents",
            "Balanced cadence for Office work; modern Office packages and final exports are stored without extra compression.",
            ResourceProfile.Balanced,
            CompressionPreference.Zstd,
            128 * 1024,
            ["~$recycle.bin"],
            [".tmp", ".wbk", ".asd"],
            [".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".one", ".pdf", ".xps"],
            [
                new WorkloadPolicyExtensionOverride(".doc", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".xls", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".ppt", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".csv", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".rtf", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".txt", CompressionPreference.Zstd, 64 * 1024)
            ]),
        new WorkloadPolicyPreset(
            WorkloadPolicyPresetId.CadBim,
            "CAD/BIM",
            "Quiet cadence for large design models, with text interchange formats still compressed.",
            ResourceProfile.Quiet,
            CompressionPreference.Zstd,
            512 * 1024,
            ["_backup", "backup", "autosave", "temp", "tmp"],
            [".bak", ".sv$", ".dwl", ".dwl2", ".tmp"],
            [".rvt", ".rfa", ".rte", ".dwg", ".dgn", ".skp", ".nwd", ".nwc", ".3dm", ".pln", ".pla", ".max"],
            [
                new WorkloadPolicyExtensionOverride(".dxf", CompressionPreference.Zstd, 256 * 1024),
                new WorkloadPolicyExtensionOverride(".ifc", CompressionPreference.Zstd, 256 * 1024),
                new WorkloadPolicyExtensionOverride(".step", CompressionPreference.Zstd, 256 * 1024),
                new WorkloadPolicyExtensionOverride(".stp", CompressionPreference.Zstd, 256 * 1024),
                new WorkloadPolicyExtensionOverride(".iges", CompressionPreference.Zstd, 256 * 1024),
                new WorkloadPolicyExtensionOverride(".igs", CompressionPreference.Zstd, 256 * 1024)
            ]),
        new WorkloadPolicyPreset(
            WorkloadPolicyPresetId.AdobeVideo,
            "Adobe/video",
            "Quiet cadence for creative projects; media and cache artefacts avoid unnecessary recompression.",
            ResourceProfile.Quiet,
            CompressionPreference.Off,
            1024 * 1024,
            ["Media Cache", "Media Cache Files", "Peak Files", "Previews", "Preview Files", "Proxies", "Proxy", "Renders", "Render Cache", ".cache"],
            [".cfa", ".pek", ".ims", ".prmdc", ".sfk", ".tmp"],
            [".mp4", ".m4v", ".mov", ".mxf", ".avi", ".mkv", ".wmv", ".wav", ".aif", ".aiff", ".mp3", ".jpg", ".jpeg", ".png", ".webp", ".heic", ".psd", ".psb", ".ai", ".indd", ".idml"],
            [
                new WorkloadPolicyExtensionOverride(".prproj", CompressionPreference.Zstd, 128 * 1024),
                new WorkloadPolicyExtensionOverride(".aep", CompressionPreference.Zstd, 128 * 1024),
                new WorkloadPolicyExtensionOverride(".aepx", CompressionPreference.Zstd, 128 * 1024),
                new WorkloadPolicyExtensionOverride(".xml", CompressionPreference.Zstd, 128 * 1024),
                new WorkloadPolicyExtensionOverride(".edl", CompressionPreference.Zstd, 64 * 1024)
            ]),
        new WorkloadPolicyPreset(
            WorkloadPolicyPresetId.DeveloperWorkspace,
            "Developer workspace",
            "Fast cadence for source files while generated outputs and dependency caches are ignored.",
            ResourceProfile.Fast,
            CompressionPreference.Zstd,
            64 * 1024,
            [".git", ".hg", ".svn", ".vs", ".idea", "bin", "obj", "node_modules", ".next", ".nuxt", "dist", "build", "out", "target", "packages", ".gradle", ".terraform", ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".cache", "coverage", ".nyc_output"],
            [".dll", ".exe", ".pdb", ".obj", ".o", ".so", ".dylib", ".class", ".jar", ".nupkg", ".snupkg", ".pyc"],
            [".zip", ".7z", ".rar", ".gz", ".xz", ".zst", ".png", ".jpg", ".jpeg", ".webp", ".mp4", ".mov"],
            [
                new WorkloadPolicyExtensionOverride(".cs", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".ts", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".js", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".json", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".xml", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".sql", CompressionPreference.Zstd, 64 * 1024),
                new WorkloadPolicyExtensionOverride(".md", CompressionPreference.Zstd, 64 * 1024)
            ]),
        new WorkloadPolicyPreset(
            WorkloadPolicyPresetId.GenericLargeFiles,
            "Generic large files",
            "Quiet cadence with larger compression thresholds for bulky mixed data.",
            ResourceProfile.Quiet,
            CompressionPreference.Zstd,
            1024 * 1024,
            ["temp", "tmp", "cache", ".cache"],
            [".tmp", ".part", ".crdownload"],
            [".zip", ".7z", ".rar", ".gz", ".xz", ".zst", ".iso", ".vhd", ".vhdx", ".vmdk", ".qcow2", ".bak", ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf", ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".jpg", ".jpeg", ".png", ".webp"],
            [
                new WorkloadPolicyExtensionOverride(".csv", CompressionPreference.Zstd, 512 * 1024),
                new WorkloadPolicyExtensionOverride(".log", CompressionPreference.Zstd, 512 * 1024),
                new WorkloadPolicyExtensionOverride(".txt", CompressionPreference.Zstd, 512 * 1024),
                new WorkloadPolicyExtensionOverride(".json", CompressionPreference.Zstd, 512 * 1024),
                new WorkloadPolicyExtensionOverride(".xml", CompressionPreference.Zstd, 512 * 1024)
            ])
    ];

    public static IReadOnlyList<WorkloadPolicyPreset> Presets { get; } = BuiltInPresets;

    public static IReadOnlyList<WorkloadPolicyPresetOption> PresetOptions { get; } = BuiltInPresets
        .Select(preset => new WorkloadPolicyPresetOption(preset.Id, preset.DisplayName, preset.Description))
        .ToArray();

    public static WorkloadPolicyPreset Get(WorkloadPolicyPresetId id)
    {
        return BuiltInPresets.FirstOrDefault(preset => preset.Id == id) ?? BuiltInPresets[0];
    }
}

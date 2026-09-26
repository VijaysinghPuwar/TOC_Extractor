using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace TocExtractor.Desktop.Services;

/// <summary>What the window needs from the operating system. Faked in tests.</summary>
public interface IShell
{
    Task<string?> PickFolderAsync(string? startIn);

    Task<string?> PickProfileToOpenAsync(string startIn);

    Task<string?> PickProfileToSaveAsync(string startIn, string suggestedName);

    /// <summary>Show a folder in Finder or File Explorer.</summary>
    Task OpenFolderAsync(string path);

    Task CopyTextAsync(string text);

    /// <summary>A notification from the operating system, with a sound, for when the person is needed and may be looking elsewhere.</summary>
    void Notify(string title, string message);
}

/// <summary>The real dialogs and launcher, through Avalonia's storage and launcher services.</summary>
public sealed class DesktopShell(TopLevel window) : IShell
{
    private static readonly FilePickerFileType Toml = new("TOC Extractor profile") { Patterns = ["*.toml"] };

    public async Task<string?> PickFolderAsync(string? startIn)
    {
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to save chapters",
            AllowMultiple = false,
            SuggestedStartLocation = await this.FolderAsync(startIn).ConfigureAwait(true),
        }).ConfigureAwait(true);

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProfileToOpenAsync(string startIn)
    {
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load a profile",
            AllowMultiple = false,
            FileTypeFilter = [Toml],
            SuggestedStartLocation = await this.FolderAsync(startIn).ConfigureAwait(true),
        }).ConfigureAwait(true);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickProfileToSaveAsync(string startIn, string suggestedName)
    {
        Directory.CreateDirectory(startIn);
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save this profile",
            SuggestedFileName = suggestedName,
            DefaultExtension = "toml",
            FileTypeChoices = [Toml],
            SuggestedStartLocation = await this.FolderAsync(startIn).ConfigureAwait(true),
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }

    public async Task OpenFolderAsync(string path)
    {
        Directory.CreateDirectory(path);
        await window.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path)).ConfigureAwait(true);
    }

    public void Notify(string title, string message)
    {
        // Notification Center on a Mac. Elsewhere the window's own bar says it.
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var script = $"display notification {Quote(message)} with title {Quote(title)} sound name \"Glass\"";
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript")
            {
                ArgumentList = { "-e", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A missing notification is not worth interrupting anyone over.
        }

        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    public async Task CopyTextAsync(string text)
    {
        if (window.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    private async Task<IStorageFolder?> FolderAsync(string? path) =>
        string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)
            ? null
            : await window.StorageProvider.TryGetFolderFromPathAsync(path).ConfigureAwait(true);
}

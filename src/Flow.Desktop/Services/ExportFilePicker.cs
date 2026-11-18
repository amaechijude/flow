using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Flow.Desktop.Services;

public interface IExportFilePicker
{
    /// <summary>
    /// Shows a real save-file picker. Returns null when cancelled or unavailable.
    /// </summary>
    Task<string?> PickSaveFileAsync(string suggestedFileName, string extension, string formatLabel, CancellationToken cancellationToken = default);
}

public sealed class StorageProviderExportFilePicker : IExportFilePicker
{
    public TopLevel? TopLevel { get; set; }

    public async Task<string?> PickSaveFileAsync(string suggestedFileName, string extension, string formatLabel, CancellationToken cancellationToken = default)
    {
        var provider = TopLevel?.StorageProvider;
        if (provider is null || !provider.CanSave) return null;

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Flow usage data",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(formatLabel) { Patterns = [$"*.{extension}"] },
            ],
        }).ConfigureAwait(true);

        return file?.TryGetLocalPath();
    }
}

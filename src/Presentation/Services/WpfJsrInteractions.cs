using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using Mskpy.Domain;

namespace Mskpy.Presentation.Services;

public sealed class WpfJsrInteractions(Func<Window?> owner) : IJsrInteractions
{
    public async Task<SelectedPhoto?> ChoosePhotoAsync(string title, CancellationToken cancellationToken)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JPEG images|*.jpg;*.jpeg", Title = title };
        if (dialog.ShowDialog(owner()) != true) return null;
        return await LoadPhotoAsync(dialog.FileName, null, cancellationToken);
    }
    public Task<SelectedPhoto> RestorePhotoAsync(PhotoReference reference, CancellationToken cancellationToken)
    {
        if (reference.LocalPath.Length < 3 || reference.LocalPath[1] != ':' || !Path.IsPathFullyQualified(reference.LocalPath))
            throw new InvalidDataException("Only local image paths can be restored from a draft.");
        return LoadPhotoAsync(reference.LocalPath, reference.Sha256, cancellationToken);
    }
    public string? ChooseDraftPath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSR draft JSON|*.json", FileName = "jsr-draft.json", OverwritePrompt = true };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }
    static async Task<SelectedPhoto> LoadPhotoAsync(string path, string? expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 20 * 1024 * 1024) throw new InvalidDataException("Select a JPEG smaller than 20 MB.");
        var length = stream.Length;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (expectedHash is not null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The saved photo changed. Select it again.");
        var preview = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder is not JpegBitmapDecoder) throw new InvalidDataException("The selected file is not a genuine JPEG.");
            if ((long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 40_000_000) throw new InvalidDataException("Select a photo below 40 megapixels.");
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 260;
            input.Position = 0; image.StreamSource = input; image.EndInit(); image.Freeze();
            return image;
        }, cancellationToken);
        return new SelectedPhoto(new PhotoReference(Path.GetFullPath(path), length, hash), preview);
    }
}

using System.Windows.Media;
using Mskpy.Domain;

namespace Mskpy.Presentation.Services;

public sealed record SelectedPhoto(PhotoReference Reference, ImageSource? Preview);
public interface IJsrInteractions
{
    Task<SelectedPhoto?> ChoosePhotoAsync(string title, CancellationToken cancellationToken);
    Task<SelectedPhoto> RestorePhotoAsync(PhotoReference photo, CancellationToken cancellationToken);
    string? ChooseDraftPath();
}

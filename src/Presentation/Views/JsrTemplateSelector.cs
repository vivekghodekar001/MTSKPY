using System.Windows;
using System.Windows.Controls;
using Mskpy.Domain;
using Mskpy.Presentation.ViewModels;

namespace Mskpy.Presentation.Views;

public sealed class JsrTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? SelectionTemplate { get; set; }
    public DataTemplate? NetworksTemplate { get; set; }
    public DataTemplate? PhotosTemplate { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item is JsrFieldViewModel field
        ? field.Definition.Kind switch
        {
            JsrFieldKind.Selection => SelectionTemplate,
            JsrFieldKind.Networks => NetworksTemplate,
            JsrFieldKind.Photos => PhotosTemplate,
            _ => TextTemplate
        } : TextTemplate;
}

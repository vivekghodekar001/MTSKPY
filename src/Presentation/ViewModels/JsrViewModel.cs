using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using Mskpy.Application;
using Mskpy.Domain;
using Mskpy.Presentation.Services;

namespace Mskpy.Presentation.ViewModels;

public sealed class JsrFieldViewModel(JsrFieldDefinition definition, string value) : ObservableObject
{
    string currentValue = value, error = "";
    bool visible;
    public JsrFieldDefinition Definition { get; } = definition;
    public string Key => Definition.Key;
    public string Label => Definition.Label;
    public IReadOnlyList<string> Options => Definition.Options ?? [];
    public int MaxLength => Definition.MaxLength;
    public bool IsLongText => MaxLength >= 100;
    public string Value { get => currentValue; set => Set(ref currentValue, value ?? ""); }
    public string Error { get => error; set { if (Set(ref error, value)) Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool IsVisible { get => visible; set => Set(ref visible, value); }
}
public sealed class NetworkViewModel(string name, bool selected) : ObservableObject
{
    bool isSelected = selected;
    public string Name { get; } = name;
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
}
public sealed class JsrPhotoViewModel(string key, string label) : ObservableObject
{
    SelectedPhoto? selected;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public PhotoReference? Reference => selected?.Reference;
    public ImageSource? Preview => selected?.Preview;
    public string FileName => Reference is null ? "No photo selected" : System.IO.Path.GetFileName(Reference.LocalPath);
    public bool HasPhoto => Reference is not null;
    public void Select(SelectedPhoto? photo)
    {
        selected = photo;
        foreach (var name in new[] { nameof(Reference), nameof(Preview), nameof(FileName), nameof(HasPhoto) }) Changed(name);
    }
}

public sealed class JsrViewModel : ObservableObject, IDisposable
{
    readonly JsrWorkflow workflow;
    readonly IJsrInteractions interaction;
    readonly JsrDocument original;
    bool busy, dirty, hasValidated;
    string status = "Complete the survey fields. Live submission remains under verification.", error = "";

    public JsrViewModel(JsrDocument document, JsrWorkflow workflow, IJsrInteractions interaction)
    {
        original = document; this.workflow = workflow; this.interaction = interaction;
        foreach (var definition in JsrForm.Fields)
        {
            var field = new JsrFieldViewModel(definition, document.Draft.Answers.GetValueOrDefault(definition.Key,
                definition.Kind == JsrFieldKind.Selection ? "Select" : ""));
            field.PropertyChanged += FieldChanged; Fields.Add(field);
        }
        foreach (var name in JsrForm.NetworkProviders)
        {
            var network = new NetworkViewModel(name, document.Draft.SelectedNetworks.Contains(name));
            network.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(NetworkViewModel.IsSelected)) Edited(); };
            Networks.Add(network);
        }
        foreach (var role in JsrForm.PhotoRoles) Photos.Add(new JsrPhotoViewModel(role.Key, role.Label));
        Validate = new RelayCommand(_ => ValidateNow(), _ => !IsBusy);
        ChoosePhoto = new AsyncCommand(ChoosePhotoAsync, ShowError, p => !IsBusy && p is JsrPhotoViewModel);
        RemovePhoto = new RelayCommand(p => { if (p is JsrPhotoViewModel photo) { photo.Select(null); Edited(); } }, _ => !IsBusy);
        SaveDraft = new AsyncCommand((_, ct) => SaveAsync(false, ct), ShowError, _ => !IsBusy);
        ValidateAndSave = new AsyncCommand((_, ct) => SaveAsync(true, ct), ShowError, _ => !IsBusy);
        Cancel = new RelayCommand(_ => { ChoosePhoto.Cancel(); SaveDraft.Cancel(); ValidateAndSave.Cancel(); }, _ => IsBusy);
        RefreshVisibility();
    }

    public JsrContext Context => original.Context;
    public ObservableCollection<JsrFieldViewModel> Fields { get; } = [];
    public ObservableCollection<NetworkViewModel> Networks { get; } = [];
    public ObservableCollection<JsrPhotoViewModel> Photos { get; } = [];
    public ObservableCollection<string> ValidationMessages { get; } = [];
    public bool HasValidationMessages => ValidationMessages.Count > 0;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Error { get => error; private set { if (Set(ref error, value)) Changed(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Changed(nameof(IsIdle)); RefreshCommands(); } } }
    public bool IsIdle => !IsBusy;
    public bool IsDirty { get => dirty; private set => Set(ref dirty, value); }
    public RelayCommand Validate { get; }
    public RelayCommand RemovePhoto { get; }
    public RelayCommand Cancel { get; }
    public AsyncCommand ChoosePhoto { get; }
    public AsyncCommand SaveDraft { get; }
    public AsyncCommand ValidateAndSave { get; }

    public JsrDraft CaptureDraft() => new(original.Draft.SourceDocumentJson, Values(),
        Networks.Where(x => x.IsSelected).Select(x => x.Name).ToArray(),
        Photos.Where(x => x.Reference is not null).ToDictionary(x => x.Key, x => x.Reference!));

    public async Task RestorePhotosAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        var failed = 0;
        try
        {
            foreach (var photo in Photos)
                if (original.Draft.Photos.TryGetValue(photo.Key, out var reference))
                {
                    try { photo.Select(await interaction.RestorePhotoAsync(reference, cancellationToken)); }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or FormatException)
                    { failed++; }
                }
            if (failed > 0) Status = $"{failed} saved photo(s) could not be restored. Choose those images again.";
        }
        finally { IsBusy = false; }
    }
    void FieldChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(JsrFieldViewModel.Value)) return;
        RefreshVisibility(); Edited();
    }
    Dictionary<string, string> Values() => Fields.Where(x => !x.Key.StartsWith("__")).ToDictionary(x => x.Key, x => x.Value);
    void RefreshVisibility()
    {
        var values = Values();
        foreach (var field in Fields) field.IsVisible = JsrForm.IsVisible(field.Definition, values, Context.IsWorkflow122);
    }
    void Edited()
    {
        IsDirty = true;
        if (hasValidated) ApplyValidation();
    }
    IReadOnlyDictionary<string, string> ApplyValidation()
    {
        var errors = workflow.Validate(CaptureDraft());
        foreach (var field in Fields)
            field.Error = errors.GetValueOrDefault(field.Key, field.Key == "__networks" ? errors.GetValueOrDefault("networks", "") : field.Key == "__photos" ? errors.GetValueOrDefault("photos", "") : "");
        ValidationMessages.Clear();
        foreach (var error in errors)
        {
            var label = Fields.FirstOrDefault(x => x.Key == error.Key)?.Label ?? (error.Key == "photos" ? "Site photos" : error.Key == "networks" ? "Available networks" : error.Key);
            ValidationMessages.Add(label + ": " + error.Value);
        }
        Changed(nameof(HasValidationMessages));
        return errors;
    }
    void ValidateNow()
    {
        Error = ""; hasValidated = true;
        Status = ApplyValidation().Count == 0 ? "Form checks passed. This does not verify location or live submission." : "Review the highlighted fields and photo requirements.";
    }
    async Task ChoosePhotoAsync(object? value, CancellationToken cancellationToken)
    {
        if (value is not JsrPhotoViewModel photo) return;
        IsBusy = true; Error = "";
        try
        {
            var selected = await interaction.ChoosePhotoAsync(photo.Label, cancellationToken);
            if (selected is not null) { photo.Select(selected); Edited(); }
        }
        finally { IsBusy = false; }
    }
    async Task SaveAsync(bool requireValid, CancellationToken cancellationToken)
    {
        Error = "";
        if (requireValid)
        {
            hasValidated = true;
            if (ApplyValidation().Count > 0) { Status = "Validation failed. Review all fields before saving a validated draft."; return; }
        }
        var path = interaction.ChooseDraftPath();
        if (path is null) return;
        IsBusy = true;
        try
        {
            await workflow.SaveDraftAsync(path, CaptureDraft(), cancellationToken);
            IsDirty = false; Status = "JSR draft saved locally. No survey was uploaded.";
        }
        finally { IsBusy = false; }
    }
    void ShowError(Exception exception)
    {
        Error = exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or FormatException
            ? exception.Message : "The survey operation could not be completed.";
    }
    void RefreshCommands()
    {
        Validate?.Refresh(); RemovePhoto?.Refresh(); ChoosePhoto?.Refresh(); SaveDraft?.Refresh(); ValidateAndSave?.Refresh(); Cancel?.Refresh();
    }
    public void Dispose() { ChoosePhoto.Cancel(); SaveDraft.Cancel(); ValidateAndSave.Cancel(); }
}

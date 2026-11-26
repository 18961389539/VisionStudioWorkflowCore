using System.Windows;

namespace VisionStudio.Wpf.Views;

public partial class InputPromptWindow : Window
{
    private readonly Func<string, string, bool, string?>? _validate;

    public InputPromptWindow(
        string title,
        string primaryLabel,
        string primaryDefault = "",
        string? secondaryLabel = null,
        string secondaryDefault = "",
        string? description = null,
        string? checkboxLabel = null,
        bool checkboxDefault = false,
        Func<string, string, bool, string?>? validate = null)
    {
        WindowTitle = title;
        PrimaryLabel = primaryLabel;
        PrimaryValue = primaryDefault;
        SecondaryLabel = secondaryLabel ?? string.Empty;
        SecondaryValue = secondaryDefault;
        ShowSecondary = !string.IsNullOrWhiteSpace(secondaryLabel);
        PromptDescription = description ?? string.Empty;
        HasDescription = !string.IsNullOrWhiteSpace(description);
        CheckboxLabel = checkboxLabel ?? string.Empty;
        CheckboxValue = checkboxDefault;
        ShowCheckbox = !string.IsNullOrWhiteSpace(checkboxLabel);
        _validate = validate;

        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            PrimaryTextBox.Focus();
            PrimaryTextBox.SelectAll();
        };
    }

    public string WindowTitle { get; }
    public string PromptDescription { get; }
    public bool HasDescription { get; }
    public string PrimaryLabel { get; }
    public string PrimaryValue { get; set; }
    public string SecondaryLabel { get; }
    public string SecondaryValue { get; set; }
    public bool ShowSecondary { get; }
    public string CheckboxLabel { get; }
    public bool CheckboxValue { get; set; }
    public bool ShowCheckbox { get; }

    public string ErrorText { get; private set; } = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PrimaryValue))
        {
            ShowError($"“{PrimaryLabel}”不能为空。");
            return;
        }

        var error = _validate?.Invoke(PrimaryValue.Trim(), SecondaryValue.Trim(), CheckboxValue);
        if (!string.IsNullOrWhiteSpace(error))
        {
            ShowError(error);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText = message;
        DataContext = null;
        DataContext = this;
    }
}

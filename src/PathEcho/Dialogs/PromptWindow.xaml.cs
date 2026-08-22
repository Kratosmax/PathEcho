using System.Windows;

namespace PathEcho.Dialogs;

public enum PromptChoice
{
    None,
    Primary,
    Secondary,
    Tertiary,
}

public partial class PromptWindow : Window
{
    public PromptWindow(
        Window owner,
        string title,
        string message,
        string primaryText,
        string? secondaryText = null,
        string? tertiaryText = null,
        bool primaryIsDanger = false)
    {
        Owner = owner;
        InitializeComponent();
        WindowBackdrop.Attach(this);
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;
        if (primaryIsDanger)
        {
            PrimaryButton.Style = (Style)FindResource("DangerButton");
        }

        if (secondaryText is not null)
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        if (tertiaryText is not null)
        {
            TertiaryButton.Content = tertiaryText;
            TertiaryButton.Visibility = Visibility.Visible;
        }
    }

    public PromptChoice Choice { get; private set; }

    private void OnPrimary(object sender, RoutedEventArgs e) => CloseWith(PromptChoice.Primary);
    private void OnSecondary(object sender, RoutedEventArgs e) => CloseWith(PromptChoice.Secondary);
    private void OnTertiary(object sender, RoutedEventArgs e) => CloseWith(PromptChoice.Tertiary);

    private void CloseWith(PromptChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}

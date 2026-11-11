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
        // Background backup retries may be the first interactive window of a tray-only session.
        if (!owner.IsVisible) owner.Show();
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
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

    public bool? ShowDialog(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(() =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(() => { if (IsVisible) Close(); });
            }
        });
        var result = base.ShowDialog();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => CloseWith(PromptChoice.Primary);
    private void OnSecondary(object sender, RoutedEventArgs e) => CloseWith(PromptChoice.Secondary);
    private void OnTertiary(object sender, RoutedEventArgs e) => CloseWith(PromptChoice.Tertiary);

    private void CloseWith(PromptChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}

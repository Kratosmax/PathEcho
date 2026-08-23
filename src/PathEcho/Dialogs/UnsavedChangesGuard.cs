using System.ComponentModel;
using System.Windows;

namespace PathEcho.Dialogs;

internal sealed class UnsavedChangesGuard
{
    private readonly Window _window;
    private readonly Func<string> _captureState;
    private string _initialState = string.Empty;
    private bool _saved;

    public UnsavedChangesGuard(Window window, Func<string> captureState)
    {
        _window = window;
        _captureState = captureState;
        window.Loaded += (_, _) => _initialState = captureState();
        window.Closing += OnClosing;
    }

    public void MarkSaved() => _saved = true;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saved || string.Equals(_initialState, _captureState(), StringComparison.Ordinal))
        {
            return;
        }

        e.Cancel = !ConfirmDiscard(_window);
    }

    public static bool ConfirmDiscard(Window owner)
    {
        var prompt = new PromptWindow(
            owner,
            "放弃未保存的更改？",
            "当前编辑内容尚未保存。关闭后，这些更改将丢失。",
            "继续编辑",
            "放弃更改",
            primaryIsDanger: false);
        prompt.ShowDialog();
        return prompt.Choice == PromptChoice.Secondary;
    }
}

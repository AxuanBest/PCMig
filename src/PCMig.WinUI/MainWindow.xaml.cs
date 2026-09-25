using Microsoft.UI.Xaml;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI;

public sealed partial class MainWindow : Window
{
    public ConnectionViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ViewModel.Shares.CollectionChanged += (_, _) => UpdateEmptySharesHint();
        UpdateEmptySharesHint();
        Closed += (_, _) => ViewModel.Dispose();
    }

    private void UpdateEmptySharesHint() =>
        EmptySharesHint.Visibility = ViewModel.Shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsConnecting) await ViewModel.ConnectAsync(PasswordInput.Password);
    }

    private async void AddShare_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsConnecting) await ViewModel.AddManualShareAsync(PasswordInput.Password);
    }

    private void NextStep_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NotifyNextStepUnavailable();
}
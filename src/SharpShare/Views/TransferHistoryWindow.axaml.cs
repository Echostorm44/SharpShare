using Avalonia.Controls;
using SharpShare.Storage;

namespace SharpShare.Views;

public partial class TransferHistoryWindow : Window
{
    public TransferHistoryWindow()
    {
        InitializeComponent();

        var entries = TransferHistoryStore.GetAll();
        HistoryList.ItemsSource = entries;

        ClearButton.Click += (_, _) =>
        {
            TransferHistoryStore.Clear();
            HistoryList.ItemsSource = Array.Empty<TransferHistoryEntry>();
        };
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.Models;
using ScreenLens.WinUI.ViewModels;
using Windows.Foundation;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class HotkeysPage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public HotkeysPage()
        {
            InitializeComponent();
        }

        /// <summary>编辑快捷键：ContentDialog 输入新组合，保存到内存演示值。</summary>
        private void OnEditHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: HotkeyEntry entry }) return;

            var input = new TextBox
            {
                Text = entry.Keys,
                Header = "新快捷键组合（如 Ctrl + Alt + A）",
                PlaceholderText = "Ctrl + Alt + A",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var dialog = new ContentDialog
            {
                Title = $"编辑快捷键 — {entry.Action}",
                Content = input,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var op = dialog.ShowAsync();
            op.Completed = (info, status) =>
            {
                if (status != AsyncStatus.Completed) return;
                if (info.GetResults() != ContentDialogResult.Primary) return;

                var keys = input.Text?.Trim();
                if (string.IsNullOrWhiteSpace(keys)) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    entry.Keys = keys;
                    Vm.ShowToast($"已更新「{entry.Action}」的演示快捷键（不注册系统热键）");
                });
            };
        }

        /// <summary>恢复默认：重置所有演示值并刷新界面显示。</summary>
        private void OnResetHotkeysClick(object sender, RoutedEventArgs e)
        {
            Vm.ResetHotkeys();
            Vm.ShowToast("已恢复默认快捷键（演示值已重置）");
        }
    }
}

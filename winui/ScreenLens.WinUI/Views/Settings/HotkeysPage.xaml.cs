using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.Models;
using ScreenLens.WinUI.Services;
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
            Loaded += (_, _) => SyncFirstRow();
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DemoSettings.HotkeyText))
                {
                    SyncFirstRow();
                }
            };
        }

        /// <summary>第一条「截图并翻译」绑定到 Vm.HotkeyText 真实值。</summary>
        private void SyncFirstRow()
        {
            Vm.Hotkeys[0].Keys = string.IsNullOrEmpty(Vm.HotkeyText)
                ? "—" : Vm.HotkeyText;
        }

        /// <summary>编辑快捷键：仅 Editable=true 的条目接后端 RegisterHotkey。</summary>
        private async void OnEditHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: HotkeyEntry entry })
            {
                return;
            }
            if (!entry.Editable)
            {
                Vm.ShowToast($"「{entry.Action}」当前版本未提供编辑能力。");
                return;
            }

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
            op.Completed = async (info, status) =>
            {
                if (status != AsyncStatus.Completed) return;
                if (info.GetResults() != ContentDialogResult.Primary) return;

                var keys = input.Text?.Trim();
                if (string.IsNullOrWhiteSpace(keys)) return;

                DispatcherQueue.TryEnqueue(async () =>
                {
                    var err = await SettingsService.SaveHotkeyAsync(keys);
                    if (err is null)
                    {
                        Vm.HotkeyText = keys;
                        Vm.ShowToast($"已保存「{entry.Action}」快捷键并通知后台代理。");
                    }
                    else
                    {
                        Vm.ShowToast(err);
                    }
                });
            };
        }

        /// <summary>恢复默认：仅作用于第一条真实可编辑的热键。</summary>
        private async void OnResetHotkeysClick(object sender, RoutedEventArgs e)
        {
            const string defaultKey = "Ctrl + Alt + A";
            var err = await SettingsService.SaveHotkeyAsync(defaultKey);
            if (err is null)
            {
                Vm.HotkeyText = defaultKey;
                Vm.ShowToast("已恢复默认快捷键（其余条目当前版本暂未提供）。");
            }
            else
            {
                Vm.ShowToast(err);
            }
        }
    }
}
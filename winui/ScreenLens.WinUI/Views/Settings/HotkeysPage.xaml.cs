using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.Models;
using ScreenLens.WinUI.Services;
using ScreenLens.WinUI.ViewModels;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.System;
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

        /// <summary>编辑快捷键：仅 Editable=true 的条目接后端 RegisterHotkey。</summary>
        private void OnEditHotkeyClick(object sender, RoutedEventArgs e)
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

            string? capturedKeys = null;
            var input = new TextBox
            {
                Text = "请按下快捷键组合…",
                Header = "按键录制",
                PlaceholderText = "例如 Ctrl + Alt + A",
                IsReadOnly = true,
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
                IsPrimaryButtonEnabled = false,
            };
            input.KeyDown += (_, keyEvent) =>
            {
                // Esc 仍由 ContentDialog 处理为取消。
                if (keyEvent.Key == VirtualKey.Escape) return;

                var combination = BuildHotkeyDisplay(keyEvent.Key);
                if (combination is null)
                {
                    input.Text = "请同时按住 Ctrl、Alt、Shift 或 Win，再按一个键";
                    keyEvent.Handled = true;
                    return;
                }

                capturedKeys = combination;
                input.Text = combination;
                dialog.IsPrimaryButtonEnabled = true;
                keyEvent.Handled = true;
            };
            input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);

            var operation = dialog.ShowAsync();
            operation.Completed = (info, status) =>
            {
                if (status != AsyncStatus.Completed
                    || info.GetResults() != ContentDialogResult.Primary
                    || string.IsNullOrWhiteSpace(capturedKeys)) return;

                DispatcherQueue.TryEnqueue(async () =>
                {
                    var err = await SettingsService.SaveHotkeyAsync(capturedKeys);
                    if (err is null)
                    {
                        Vm.HotkeyText = capturedKeys;
                        Vm.ShowToast($"已保存「{entry.Action}」快捷键并通知后台代理。");
                    }
                    else
                    {
                        Vm.ShowToast(err);
                    }
                });
            };
        }

        private static string? BuildHotkeyDisplay(VirtualKey key)
        {
            var virtualKey = (int)key;
            if (virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
                return null; // modifier-only presses are not complete shortcuts

            var parts = new List<string>();
            if (IsKeyDown(0x11)) parts.Add("Ctrl");
            if (IsKeyDown(0x10)) parts.Add("Shift");
            if (IsKeyDown(0x12)) parts.Add("Alt");
            if (IsKeyDown(0x5B) || IsKeyDown(0x5C)) parts.Add("Win");
            if (parts.Count == 0) return null;

            var mainKey = GetKeyboardLibraryName(virtualKey, IsKeyDown(0x10));
            if (mainKey is null) return null;
            parts.Add(mainKey);
            return string.Join(" + ", parts);
        }

        private static string? GetKeyboardLibraryName(int key, bool shiftDown)
        {
            if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
                return ((char)key).ToString();
            if (key is >= 0x70 and <= 0x87)
                return $"F{key - 0x6F}";
            if (key is >= 0x60 and <= 0x69)
                return $"Num {key - 0x60}";

            return key switch
            {
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0D => "Enter",
                0x13 => "Pause",
                0x14 => "Caps Lock",
                0x20 => "Space",
                0x21 => "Page Up",
                0x22 => "Page Down",
                0x23 => "End",
                0x24 => "Home",
                0x25 => "Left",
                0x26 => "Up",
                0x27 => "Right",
                0x28 => "Down",
                0x2C => "Print Screen",
                0x2D => "Insert",
                0x2E => "Delete",
                0x6A => "Num Multiply",
                0x6B => "Num Add",
                0x6D => "Num Minus",
                0x6E => ".",
                0x6F => "Num Divide",
                0xBA => ";",
                0xBB => shiftDown ? "Plus" : "=",
                0xBC => ",",
                0xBD => "-",
                0xBE => ".",
                0xBF => "/",
                0xC0 => "`",
                0xDB => "[",
                0xDC => "\\",
                0xDD => "]",
                0xDE => "'",
                _ => null,
            };
        }

        private static bool IsKeyDown(int virtualKey)
            => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

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

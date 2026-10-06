using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.Models;
using ScreenLens.WinUI.Services;
using ScreenLens.WinUI.ViewModels;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.System;
using System;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class HotkeysPage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public HotkeysPage()
        {
            InitializeComponent();
        }

        private bool _editing;

        private async void OnEditHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (_editing) return;
            if (sender is not FrameworkElement { DataContext: HotkeyEntry entry })
            {
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
                SecondaryButtonText = entry.Id == "capture" ? "" : "清除快捷键",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
                // A dialog is hosted in a separate popup tree. Resolve its
                // theme before it appears rather than inheriting it on load.
                RequestedTheme = ActualTheme,
                Style = (Style)Resources["HotkeyEditorDialogStyle"],
                IsPrimaryButtonEnabled = false,
            };
            input.PreviewKeyDown += (_, keyEvent) =>
            {
                if (keyEvent.Key == VirtualKey.Escape && entry.Id != "cancel_capture") return;

                var combination = BuildHotkeyDisplay(keyEvent.Key);
                if (combination is null)
                {
                    input.Text = "请按组合键或 F1–F24";
                    keyEvent.Handled = true;
                    return;
                }

                capturedKeys = combination;
                input.Text = combination;
                dialog.IsPrimaryButtonEnabled = true;
                keyEvent.Handled = true;
            };
            input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);

            _editing = true;
            try
            {
                await BackendClient.Instance.CallAsync("SetHotkeyRecording", new System.Text.Json.Nodes.JsonObject
                {
                    ["active"] = true,
                });
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.None) return;
                if (result == ContentDialogResult.Primary && string.IsNullOrEmpty(capturedKeys)) return;
                var err = await SettingsService.SaveHotkeyAsync(
                    result == ContentDialogResult.Secondary ? "" : capturedKeys!, entry.Id);
                if (err is null)
                {
                    Vm.ShowToast(result == ContentDialogResult.Secondary
                        ? $"已清除「{entry.Action}」快捷键" : $"已保存「{entry.Action}」快捷键");
                }
                else Vm.ShowToast(err);
            }
            catch (Exception ex) { Vm.ShowToast(ex.Message); }
            finally
            {
                try
                {
                    await BackendClient.Instance.CallAsync("SetHotkeyRecording", new System.Text.Json.Nodes.JsonObject
                    {
                        ["active"] = false,
                    }, timeoutMs: 2000);
                }
                catch { /* The recording lease expires even if the agent disconnects. */ }
                _editing = false;
            }
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
            if (parts.Count == 0 && key != VirtualKey.Escape
                && virtualKey is not (>= 0x70 and <= 0x87) && virtualKey != 0x2C) return null;

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
                0x1B => "Esc",
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

        private async void OnResetHotkeysClick(object sender, RoutedEventArgs e)
        {
            if (_editing) return;
            _editing = true;
            var err = await SettingsService.ResetHotkeysAsync();
            _editing = false;
            if (err is null)
            {
                Vm.ShowToast("已恢复默认快捷键");
            }
            else
            {
                Vm.ShowToast(err);
            }
        }
    }
}

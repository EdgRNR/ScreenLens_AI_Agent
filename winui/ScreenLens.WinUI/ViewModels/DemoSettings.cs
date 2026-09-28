using ScreenLens.WinUI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenLens.WinUI.ViewModels
{
    /// <summary>
    /// 前端演示状态：仅存在于当前进程内存中，不做任何持久化。
    /// 页面通过 x:Bind 双向绑定到本单例，控件操作即时反映。
    /// </summary>
    public sealed class DemoSettings : INotifyPropertyChanged
    {
        public static DemoSettings Instance { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private DemoSettings() { }

        // ---------- 通用 ----------
        /// <summary>0 = 跟随系统，1 = 浅色，2 = 深色（默认）</summary>
        private int _themeMode = 2;
        public int ThemeMode
        {
            get => _themeMode;
            set { if (Set(ref _themeMode, value)) ThemeModeChanged?.Invoke(value); }
        }

        public event Action<int>? ThemeModeChanged;

        private bool _launchAtStartup;
        public bool LaunchAtStartup { get => _launchAtStartup; set => Set(ref _launchAtStartup, value); }

        private bool _startMinimized;
        public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

        // ---------- 截图与选区 ----------
        /// <summary>0 = 矩形选区，1 = 自由圈选</summary>
        private int _defaultCaptureMode;
        public int DefaultCaptureMode { get => _defaultCaptureMode; set => Set(ref _defaultCaptureMode, value); }

        /// <summary>开启：松开鼠标显示确认工具条；关闭：松开鼠标直接开始识别（演示中直接打开结果预览）</summary>
        private bool _confirmOnRelease = true;
        public bool ConfirmOnRelease { get => _confirmOnRelease; set => Set(ref _confirmOnRelease, value); }

        // ---------- OCR 与结果 ----------
        /// <summary>0 = 跟随截图位置，1 = 屏幕居中，2 = 记住上次位置</summary>
        private int _resultPosition;
        public int ResultPosition { get => _resultPosition; set => Set(ref _resultPosition, value); }

        /// <summary>0 = 小，1 = 中（默认），2 = 大</summary>
        private int _originalFontSize = 1;
        public int OriginalFontSize { get => _originalFontSize; set => Set(ref _originalFontSize, value); }

        /// <summary>0 = 图标 + 文字，1 = 仅图标</summary>
        private int _copyButtonLook;
        public int CopyButtonLook { get => _copyButtonLook; set => Set(ref _copyButtonLook, value); }

        // ---------- 翻译 ----------
        private int _targetLanguage;
        public int TargetLanguage { get => _targetLanguage; set => Set(ref _targetLanguage, value); }

        /// <summary>0 = Google 免费，1 = OpenAI 兼容，2 = DeepSeek，3 = 关闭</summary>
        private int _provider = 2;
        public int Provider { get => _provider; set => Set(ref _provider, value); }

        // ---------- 外观 ----------
        /// <summary>0 = 舒适（默认），1 = 紧凑</summary>
        private int _cardDensity;
        public int CardDensity
        {
            get => _cardDensity;
            set { if (Set(ref _cardDensity, value)) CardDensityChanged?.Invoke(value); }
        }

        public event Action<int>? CardDensityChanged;

        private int _accentIndex;
        public int AccentIndex
        {
            get => _accentIndex;
            set { if (Set(ref _accentIndex, value)) AccentChanged?.Invoke(value); }
        }

        /// <summary>强调色变化：由 App.ApplyAccent 应用到主题资源</summary>
        public event Action<int>? AccentChanged;

        // ---------- 快捷键（演示值） ----------

        public ObservableCollection<HotkeyEntry> Hotkeys { get; } = new()
        {
            new HotkeyEntry { Icon = "\uE722", Action = "截图并翻译", Description = "启动区域截图，识别后立即翻译", Keys = "Ctrl + Alt + A" },
            new HotkeyEntry { Icon = "\uE8A5", Action = "截图并识别", Description = "仅识别选区文字，不进行翻译", Keys = "Ctrl + Alt + O" },
            new HotkeyEntry { Icon = "\uE713", Action = "打开设置", Description = "打开 ScreenLens 设置窗口", Keys = "Ctrl + Alt + S" },
            new HotkeyEntry { Icon = "\uE7E7", Action = "退出截图", Description = "取消进行中的截图并关闭遮罩", Keys = "Esc" },
        };

        public void ResetHotkeys()
        {
            Hotkeys[0].Keys = "Ctrl + Alt + A";
            Hotkeys[1].Keys = "Ctrl + Alt + O";
            Hotkeys[2].Keys = "Ctrl + Alt + S";
            Hotkeys[3].Keys = "Esc";
        }

        // ---------- 轻量演示提示 ----------
        public event Action<string>? ToastRequested;

        /// <summary>对尚未实现的操作给出统一提示。</summary>
        public void ShowToast(string message) => ToastRequested?.Invoke(message);
    }
}

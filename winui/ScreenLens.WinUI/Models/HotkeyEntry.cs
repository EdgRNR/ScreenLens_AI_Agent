using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenLens.WinUI.Models
{
    /// <summary>
    /// 快捷键条目：Editable = true 的条目接后端真实注册，
    /// 其余为未实现能力，仅作展示。
    /// </summary>
    public sealed class HotkeyEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Icon { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;

        /// <summary>是否可编辑（接后端 RegisterHotkey）。</summary>
        public bool Editable { get; init; }

        private string _keys = string.Empty;
        public string Keys
        {
            get => _keys;
            set { if (_keys != value) { _keys = value; Raise(); } }
        }
    }
}

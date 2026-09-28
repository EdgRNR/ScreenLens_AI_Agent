using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenLens.WinUI.Models
{
    /// <summary>
    /// 快捷键演示条目：仅存在于内存中，不会注册到操作系统。
    /// </summary>
    public sealed class HotkeyEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Icon { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;

        private string _keys = string.Empty;
        public string Keys
        {
            get => _keys;
            set { if (_keys != value) { _keys = value; Raise(); } }
        }
    }
}

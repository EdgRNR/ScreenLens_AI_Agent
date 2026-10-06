using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenLens.WinUI.Models
{
    /// <summary>
    /// 可配置的快捷键操作。
    /// </summary>
    public sealed class HotkeyEntry : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Icon { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
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

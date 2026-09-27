using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ScreenLens.WinUI.Controls
{
    /// <summary>
    /// 可复用设置卡片：图标、名称、简短说明与操作控件排在同一行。
    /// Content 直接作为右侧操作槽使用。
    /// </summary>
    public sealed partial class SettingCard : UserControl
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty));

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        public SettingCard()
        {
            InitializeComponent();
        }

        public Visibility DescToVis(string? description)
            => string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
    }
}

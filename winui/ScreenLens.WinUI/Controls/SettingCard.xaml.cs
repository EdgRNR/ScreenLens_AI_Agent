using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Controls
{
    /// <summary>
    /// 可复用设置卡片：图标、名称、简短说明与操作控件排在同一行。
    /// 直接写入卡片 XAML 内容的控件会显示在右侧操作槽（Content）。
    /// 卡片密度跟随 DemoSettings 即时变化（舒适/紧凑）。
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
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        // ---------- 卡片密度即时生效（舒适 16,14 / 紧凑 12,8） ----------

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyDensity(DemoSettings.Instance.CardDensity);
            DemoSettings.Instance.CardDensityChanged += ApplyDensity;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DemoSettings.Instance.CardDensityChanged -= ApplyDensity;
        }

        private void ApplyDensity(int density)
        {
            if (density == 1) // 紧凑
            {
                CardRoot.Padding = new Thickness(12, 8, 12, 8);
                CardRoot.MinHeight = 48;
            }
            else // 舒适
            {
                CardRoot.Padding = new Thickness(16, 14, 16, 14);
                CardRoot.MinHeight = 68;
            }
        }

        public Visibility DescToVis(string? description)
            => string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
    }
}

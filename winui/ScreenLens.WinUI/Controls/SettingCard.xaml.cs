using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Controls
{
    /// <summary>
    /// 可复用设置卡片：图标、名称、简短说明与操作控件排在同一行（窄窗口时操作控件换行到下方）。
    ///
    /// 注意：WinUI 的 UserControl 会把 XAML 子元素写入 Content 属性，直接写在
    /// SettingCard 标签内的控件会覆盖 InitializeComponent 载入的卡片视觉树。
    /// 因此这里用 ContentProperty 把子元素重定向到 ActionContent，再由卡片内部的
    /// ContentPresenter 呈现，卡片本体（边框 / 图标 / 标题 / 说明）才能保留。
    /// </summary>
    [ContentProperty(Name = nameof(ActionContent))]
    public sealed partial class SettingCard : UserControl
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty));

        /// <summary>右侧（窄窗口时下方）操作控件槽。</summary>
        public static readonly DependencyProperty ActionContentProperty =
            DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(SettingCard), new PropertyMetadata(null));

        public static readonly DependencyProperty DetailsContentProperty =
            DependencyProperty.Register(nameof(DetailsContent), typeof(object), typeof(SettingCard), new PropertyMetadata(null));

        /// <summary>卡片内缩进的子设置，继承同一卡片的主题和生命周期。</summary>
        public object? DetailsContent
        {
            get => GetValue(DetailsContentProperty);
            set => SetValue(DetailsContentProperty, value);
        }

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

        public object? ActionContent
        {
            get => GetValue(ActionContentProperty);
            set => SetValue(ActionContentProperty, value);
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

        // ContentPresenter 将操作控件和子设置接入当前窗口的视觉树，自动继承主题。
        // 不在 ActualThemeChanged 中再给后代设置 RequestedTheme：这会干扰正在进行
        // 的主题传播，造成窗口已切换、后代仍保留上一种主题的状态。

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

        public Visibility DetailsToVis(object? details)
            => details is null ? Visibility.Collapsed : Visibility.Visible;
    }
}

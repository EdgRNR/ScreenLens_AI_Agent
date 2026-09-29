using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenLens.WinUI.ViewModels;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class TranslatePage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;

        public TranslatePage()
        {
            InitializeComponent();
        }

        /// <summary>仅 OpenAI 兼容（provider==1）显示连接参数表单。</summary>
        public Visibility ProviderFormVis(int provider)
            => provider == 1 ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>仅「关闭翻译」（provider==2）时显示说明。</summary>
        public Visibility ProviderOffVis(int provider)
            => provider == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}

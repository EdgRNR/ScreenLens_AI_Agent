using Microsoft.UI.Xaml.Data;
using System;

namespace ScreenLens.WinUI.Converters
{
    /// <summary>
    /// 把可用宽度钳制到上限（默认 1000 DIP）。
    ///
    /// 用途：设置页要求内容左对齐又要求各页卡片宽度一致。
    /// StackPanel 用 HorizontalAlignment="Left" 时宽度会退化为"内容自然宽度"，
    /// 导致不同页面卡片宽窄不一；这里给承载页面的 Frame 显式赋值
    /// min(可用宽度, 上限)，即可同时满足左对齐与等宽。
    /// </summary>
    public sealed class WidthClampConverter : IValueConverter
    {
        /// <summary>宽度上限（DIP）。</summary>
        public double MaxWidth { get; set; } = 1000;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            double available = value is double d ? d : 0d;
            if (available <= 0) return 0d;

            double max = MaxWidth;
            if (parameter is string text && double.TryParse(text, out double parsed) && parsed > 0)
            {
                max = parsed;
            }

            return Math.Min(available, max);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}

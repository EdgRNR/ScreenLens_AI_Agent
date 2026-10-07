using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace ScreenLens.WinUI.Controls;

/// <summary>Built-in vector outlines; settings icons never depend on a system symbol font.</summary>
internal static class SettingsIcons
{
    internal static Geometry? Get(string name, double size = 20)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var path = Application.Current.Resources["Icon" + name] as string
            ?? throw new InvalidOperationException($"设置图标资源不存在：{name}");
        return FromPath(path, size);
    }

    // Share immutable descriptions. Each icon gets its own geometry when it
    // is bound, keeping resource ownership independent across pages/windows.
    internal static Geometry FromPath(string path, double size = 20)
    {
        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
        // PathIcon clips to its layout box; Width/Height do not scale Data.
        // Our outlines use a 24-unit canvas, including space around the edges.
        var scale = size / 24.0;
        geometry.Transform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        return geometry;
    }
}

public sealed class SettingsIconGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => SettingsIcons.FromPath((string)value, parameter is string size
            ? double.Parse(size, CultureInfo.InvariantCulture) : 20);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

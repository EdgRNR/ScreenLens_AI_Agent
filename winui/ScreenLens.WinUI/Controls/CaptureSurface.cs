using System;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ScreenLens.WinUI.Controls;

public sealed class CaptureSurface : Grid, IDisposable
{
    private readonly Dictionary<InputSystemCursorShape, InputSystemCursor> _cursors = new();
    private InputSystemCursorShape? _shape;
    private bool _disposed;

    public void SetCursorShape(InputSystemCursorShape shape)
    {
        if (_disposed || _shape == shape) return;
        if (!_cursors.TryGetValue(shape, out var cursor))
        {
            cursor = InputSystemCursor.Create(shape);
            _cursors.Add(shape, cursor);
        }
        ProtectedCursor = cursor;
        _shape = shape;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ProtectedCursor = null;
        foreach (var cursor in _cursors.Values) cursor.Dispose();
        _cursors.Clear();
        _shape = null;
    }
}

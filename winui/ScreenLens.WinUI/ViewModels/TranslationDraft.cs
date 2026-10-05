using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace ScreenLens.WinUI.ViewModels;

/// <summary>设置页草稿；输入不改变正在使用的翻译配置。</summary>
public sealed class TranslationDraft : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _loading;
    private int _provider;
    private string _baseUrl = "";
    private string _apiKey = "";
    private string _model = "";
    private bool _dirty;
    public int Provider { get => _provider; set => Set(ref _provider, value); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }
    public string Model { get => _model; set => Set(ref _model, value); }
    public bool IsDirty
    {
        get => _dirty;
        private set { if (_dirty == value) return; _dirty = value; Raise(); Raise(nameof(Status)); }
    }
    public string Status => IsDirty ? "尚未保存；当前翻译仍使用上次保存的配置。" : "当前配置已保存。";
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Raise(name);
        if (!_loading) IsDirty = true;
    }
    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Load(DemoSettings vm)
    {
        _loading = true;
        try { Provider = vm.Provider; BaseUrl = vm.OpenAiBaseUrl; ApiKey = vm.OpenAiApiKey; Model = vm.OpenAiModel; IsDirty = false; }
        finally { _loading = false; }
    }
    public JsonObject OpenAiParameters() => new()
    {
        ["base_url"] = BaseUrl.Trim().TrimEnd('/'),
        ["api_key"] = ApiKey.Trim(), ["model"] = Model.Trim(),
    };
    public JsonObject Snapshot(int targetLanguage) => new()
    {
        ["provider"] = Services.SettingsService.ProviderKeys[Math.Clamp(Provider, 0, 2)],
        ["target_language"] = Services.SettingsService.LanguageKeys[Math.Clamp(targetLanguage, 0, 2)],
        ["openai"] = OpenAiParameters(),
    };
    public string? Validate(bool requireModel)
    {
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "请填写有效的 API 基础地址，如 https://api.example.com/v1。";
        if (string.IsNullOrWhiteSpace(ApiKey)) return "请填写 API 密钥。";
        if (requireModel && string.IsNullOrWhiteSpace(Model)) return "请选择或填写模型名。";
        return null;
    }
}

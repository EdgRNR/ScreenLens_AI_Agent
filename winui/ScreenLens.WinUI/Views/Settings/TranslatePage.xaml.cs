using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScreenLens.WinUI.Services;
using ScreenLens.WinUI.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLens.WinUI.Views.Settings
{
    public sealed partial class TranslatePage : Page
    {
        public DemoSettings Vm => DemoSettings.Instance;
        public TranslationDraft Draft { get; } = new();
        private List<string> _models = new();
        private CancellationTokenSource? _operationCancellation;
        private bool _busy;
        private bool _loaded;
        private bool _modelSuggestionsShown;

        public TranslatePage()
        {
            Draft.Load(Vm);
            InitializeComponent();
            UpdateModelButtons();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            if (!Draft.IsDirty) Draft.Load(Vm);
            Vm.PropertyChanged += OnActiveSettingsChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            Vm.PropertyChanged -= OnActiveSettingsChanged;
            _operationCancellation?.Cancel();
            ModelBox.IsSuggestionListOpen = false;
            _modelSuggestionsShown = false;
        }

        private void OnActiveSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!Draft.IsDirty && !_busy && e.PropertyName is nameof(Vm.Provider) or nameof(Vm.OpenAiBaseUrl) or nameof(Vm.OpenAiApiKey) or nameof(Vm.OpenAiModel))
                Draft.Load(Vm);
        }

        public Visibility ProviderFormVis(int provider) => provider == 1 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ProviderOffVis(int provider) => provider == 2 ? Visibility.Visible : Visibility.Collapsed;

        private void SetBusy(bool busy, bool cancellable = false)
        {
            _busy = busy;
            ProviderCombo.IsEnabled = ApiUrlBox.IsEnabled = ApiKeyBox.IsEnabled = ModelBox.IsEnabled = ModelsBtn.IsEnabled = TestBtn.IsEnabled =
                SaveTranslationBtn.IsEnabled = RevertTranslationBtn.IsEnabled = !busy;
            ApiProgress.IsActive = busy;
            ApiProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            CancelApiBtn.Visibility = busy && cancellable ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Status(string text, InfoBarSeverity severity)
        {
            if (!_loaded) return;
            TranslationStatus.Message = text;
            TranslationStatus.Severity = severity;
            TranslationStatus.IsOpen = true;
        }

        private void OnApiTextChanged(object sender, TextChangedEventArgs e) => ResetModels();
        private void OnApiKeyChanged(object sender, RoutedEventArgs e) => ResetModels();
        private void ResetModels()
        {
            _models.Clear();
            if (ModelBox is not null) ModelBox.ItemsSource = null;
        }

        private void FilterModels()
        {
            var query = ModelBox.Text.Trim();
            ModelBox.ItemsSource = _models.Where(m => m.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(100).ToArray();
        }
        private void OnModelTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) Draft.Model = sender.Text;
            FilterModels();
            UpdateModelButtons();
        }
        private void OnModelChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            Draft.Model = args.SelectedItem?.ToString() ?? "";
            _modelSuggestionsShown = false;
            sender.IsSuggestionListOpen = false;
            UpdateModelButtons();
        }
        private void OnModelGotFocus(object sender, RoutedEventArgs e)
        {
            FilterModels();
        }
        private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsInsideModelBox(e.OriginalSource as DependencyObject)) return;
            _modelSuggestionsShown = false;
            ModelBox.IsSuggestionListOpen = false;
        }
        private bool IsInsideModelBox(DependencyObject? source)
        {
            while (source is not null)
            {
                if (ReferenceEquals(source, ModelBox) || ReferenceEquals(source, ModelDropdownBtn)
                    || ReferenceEquals(source, ModelClearBtn)) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }
        private void OnModelDropdownClick(object sender, RoutedEventArgs e)
        {
            // Apply the query before opening so a typed model never flashes the full list.
            FilterModels();
            _modelSuggestionsShown = !_modelSuggestionsShown
                && ModelBox.ItemsSource is System.Collections.ICollection items && items.Count > 0;
            ModelBox.IsSuggestionListOpen = _modelSuggestionsShown;
        }
        private void OnModelClearClick(object sender, RoutedEventArgs e)
        {
            ModelBox.Text = "";
            Draft.Model = "";
            _modelSuggestionsShown = false;
            ModelBox.IsSuggestionListOpen = false;
            UpdateModelButtons();
        }
        private void UpdateModelButtons()
            => ModelClearBtn.Visibility = string.IsNullOrEmpty(ModelBox.Text)
                ? Visibility.Collapsed : Visibility.Visible;
        private void OnCancelApiClick(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

        private async void OnGetModelsClick(object sender, RoutedEventArgs e) => await RunSetupAsync(false);
        private async void OnTestConnectionClick(object sender, RoutedEventArgs e) => await RunSetupAsync(true);

        private async Task RunSetupAsync(bool test)
        {
            if (_busy) return;
            var error = Draft.Validate(requireModel: test);
            if (error is not null) { Status(error, InfoBarSeverity.Warning); return; }
            using var cancellation = new CancellationTokenSource();
            _operationCancellation = cancellation;
            SetBusy(true, true);
            Status(test ? "正在测试翻译连接…" : "正在获取模型列表…", InfoBarSeverity.Informational);
            try
            {
                var response = await BackendClient.Instance.CallAsync(
                    test ? "TestTranslationConnection" : "ListTranslationModels",
                    new JsonObject { ["openai"] = Draft.OpenAiParameters(),
                        ["target_language"] = SettingsService.LanguageKeys[Vm.TargetLanguage],
                        ["request_token"] = Guid.NewGuid().ToString("N") },
                    timeoutMs: 85_000, ct: cancellation.Token);
                if (!_loaded) return;
                if (test)
                    Status("连接测试成功，译文：" + (response["text"]?.GetValue<string>() ?? ""), InfoBarSeverity.Success);
                else
                {
                    _models = response["models"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new();
                    ModelBox.ItemsSource = _models.Take(100).ToArray();
                    _modelSuggestionsShown = false;
                    ModelBox.IsSuggestionListOpen = false;
                    UpdateModelButtons();
                    Status($"已获取 {_models.Count} 个模型，可搜索选择，也可手动填写。", InfoBarSeverity.Success);
                }
            }
            catch (OperationCanceledException) { Status("操作已取消。", InfoBarSeverity.Informational); }
            catch (BackendException ex) { Status(ex.Message, InfoBarSeverity.Error); }
            catch (Exception) { Status("操作未能完成，请检查后台代理与服务配置后重试。", InfoBarSeverity.Error); }
            finally { _operationCancellation = null; SetBusy(false); }
        }

        private async void OnSaveTranslationClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (Draft.Provider == 1 && Draft.Validate(requireModel: true) is { } error)
            { Status(error, InfoBarSeverity.Warning); return; }
            var snapshot = Draft.Snapshot(Vm.TargetLanguage);
            // A different provider does not activate or persist unfinished hidden API fields.
            if (Draft.Provider != 1)
                snapshot["openai"] = new JsonObject { ["base_url"] = Vm.OpenAiBaseUrl,
                    ["api_key"] = Vm.OpenAiApiKey, ["model"] = Vm.OpenAiModel };
            SetBusy(true);
            try
            {
                var saveError = await SettingsService.SaveTranslationAsync(Vm, snapshot);
                if (saveError is not null) { Status("保存失败：" + saveError + "（保留上次生效的配置）", InfoBarSeverity.Error); return; }
                Draft.Load(Vm);
                Status("配置已保存并启用。", InfoBarSeverity.Success);
            }
            finally { SetBusy(false); }
        }

        private void OnRevertTranslationClick(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            Draft.Load(Vm);
            ResetModels();
            TranslationStatus.IsOpen = false;
        }
    }
}

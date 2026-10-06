using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShiKe.Host;
using ShiKe.Services;

namespace ShiKe.Tools.Craft;

/// <summary>
/// 洗装页面（对齐 Python config_tab.py 全部交互逻辑）。
/// 三栏：模式与延迟 / 通货网格(2列,按模式显示子集)与坐标 / 词缀及预设。
/// 三态坐标录制（信号分离坑#10）/ 词缀池(主/次/排除+命中数实时验证) / 预设管理 / 启停。
/// </summary>
public partial class CraftPage : UserControl
{
    private sealed class CurrencyCardUi
    {
        public required string Key { get; init; }
        public required Button CoordButton { get; init; }
        public required Button ClearButton { get; init; }
    }

    private sealed class AffixTagUi
    {
        public required string Text { get; init; }
        public required Border Root { get; init; }
    }

    private readonly ToolHost _host;
    private readonly CraftTool _tool;
    private readonly Dictionary<string, CurrencyCardUi> _cards = [];
    private readonly List<AffixTagUi> _primaryTags = [];
    private readonly List<AffixTagUi> _secondaryTags = [];
    private readonly List<AffixTagUi> _excludeTags = [];
    private bool _refreshingPresets;

    private string _selectedCurrency = Currency.Alteration;
    private bool _dirty;
    private bool _initialized;   // Loaded 防重复（WPF Loaded 在每次进入可视树时触发，卡片会翻倍）
    private bool _restoringHitCount;
    private bool _refreshingEnabled;
    private int _lastValidPrimaryHit;
    private int _lastValidSecondaryHit;

    public CraftPage(ToolHost host, CraftTool tool)
    {
        InitializeComponent();
        _host = host;
        _tool = tool;
        RefreshHotkeyHints(tool.HotkeyStart, tool.HotkeyStop, host.CoordinateHotkey);

        Loaded += (_, _) => OnLoaded();
    }

    /// <summary>由 SettingsTool 在热键成功重注册后调用；不收集或改写页面业务配置。</summary>
    internal void RefreshHotkeyHints(string startHotkey, string stopHotkey, string coordinateHotkey)
    {
        CraftHotkeyHint.Text = $"启动 {HotkeySetting.Display(startHotkey)} · 停止 {HotkeySetting.Display(stopHotkey)}";
        if (_host.Coordinates.ActiveSlot is not { } slot) return;
        if (string.IsNullOrWhiteSpace(coordinateHotkey))
        {
            _host.Coordinates.CancelRecording();
            UpdateCoordButton(slot.SlotId, _host.Coordinates.GetCoordinate(slot.SlotId));
        }
        else if (slot.SlotId == "item")
            ItemCoordButton.Content = $"移动鼠标后按 {coordinateHotkey}...";
        else if (_cards.TryGetValue(slot.SlotId, out var card))
            card.CoordButton.Content = $"移动鼠标后按 {coordinateHotkey}...";
    }

    internal void RefreshEnabledPresentation()
    {
        _refreshingEnabled = true;
        try
        {
            EnableToolSwitch.IsChecked = _tool.IsEnabled;
            CraftHotkeyHint.Opacity = _tool.IsEnabled ? 1.0 : 0.55;
            CraftStatusText.Text = _tool.IsEnabled ? (CraftStatusText.Text == "功能已停用" ? "就绪" : CraftStatusText.Text) : "功能已停用";
        }
        finally
        {
            _refreshingEnabled = false;
        }
    }

    private CraftMode CurrentMode => _tool.Rules.Mode;

    private void OnLoaded()
    {
        if (_initialized) return; // 防重复：切走再切回不重建
        _initialized = true;
        var startupTimer = Stopwatch.StartNew();

        BuildCurrencyCards();

        // 坐标录制完成 → 更新按钮三态 + 同步到引擎坐标字典（修复：F7 录制后 UI 不更新）
        _host.Coordinates.RecordingCompleted += (slot, pt) => Dispatcher.BeginInvoke(() =>
        {
            _tool.Coordinates[slot.SlotId] = pt;
            UpdateCoordButton(slot.SlotId, pt);
        });
        _host.Coordinates.RecordingFailed += (slot, message) => Dispatcher.BeginInvoke(() =>
        {
            _host.Notification.ShowError($"{slot.DisplayName}录制失败：{message}");
        });

        SubscribeEngine();
        LoadStateIntoUi();
        RefreshPresetCombo();

        // 延迟滑块绑定
        DelaySlider.ValueChanged += (_, _) => DelayValue.Text = $"{(int)DelaySlider.Value} ms";
        DelaySlider.Value = _tool.DelayMs;
        DelayValue.Text = $"{_tool.DelayMs} ms";
        RefreshEnabledPresentation();
        var startupElapsedMs = startupTimer.ElapsedMilliseconds;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => Diag.Log($"[启动性能] Craft 首次页面初始化完成={startupElapsedMs}ms")));
    }

    private void EnableToolSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingEnabled || !_initialized) return;
        var result = _tool.SetEnabled(EnableToolSwitch.IsChecked == true);
        if (!result.Success)
        {
            RefreshEnabledPresentation();
            ShowOwnedMessage(result.Message, "无法修改启用状态", MessageBoxImage.Warning);
        }
    }

    private void ShowOwnedMessage(string message, string title, MessageBoxImage image)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
        else
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
    }

    // ── 通货网格 ──

    private void BuildCurrencyCards()
    {
        _cards.Clear();
        foreach (var key in Currency.All)
        {
            var coordBtn = new Button
            {
                Content = "设定坐标",
                Style = (Style)FindResource("CardButton"),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var clearBtn = new Button
            {
                Content = "✕",
                Width = 26,
                Padding = new Thickness(2),
                Visibility = Visibility.Collapsed,
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 10,
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xC0, 0x39, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xC0, 0x39, 0x2B)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0x39, 0x2B)),
            };

            var nameLabel = new TextBlock
            {
                Text = Currency.Label(key),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextPrimary"),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            nameLabel.MouseLeftButtonUp += (_, _) => SelectCurrency(key);

            var coordRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            coordRow.ColumnDefinitions.Add(new ColumnDefinition());
            coordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(clearBtn, 1);
            coordRow.Children.Add(coordBtn);
            coordRow.Children.Add(clearBtn);

            var root = new Border
            {
                Background = (Brush)FindResource("CardBackground"),
                BorderBrush = (Brush)FindResource("CardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(3),
                Child = new StackPanel
                {
                    Children = { nameLabel, coordRow },
                },
            };

            var card = new CurrencyCardUi { Key = key, CoordButton = coordBtn, ClearButton = clearBtn };
            _cards[key] = card;
            CurrencyGrid.Items.Add(root);

            coordBtn.Click += (_, _) => StartRecordSlot(key, $"{Currency.Label(key)}位置", coordBtn);
            clearBtn.Click += (_, _) => ClearSlot(key);
        }

        ApplyModeVisibility();
        UpdateAllCoordButtons();
    }

    private void ApplyModeVisibility()
    {
        var visible = Currency.ModeCurrencies.TryGetValue(CurrentMode, out var keys) ? keys : [];
        foreach (var (key, card) in _cards)
        {
            var root = (Border)CurrencyGrid.Items[Array.IndexOf(Currency.All, key)];
            root.Visibility = visible.Contains(key) ? Visibility.Visible : Visibility.Collapsed;
        }
        Mode2Frame.Visibility = CurrentMode == CraftMode.AltAug ? Visibility.Visible : Visibility.Collapsed;
        Mode3Frame.Visibility = CurrentMode == CraftMode.AltAugRegal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectCurrency(string key)
    {
        _selectedCurrency = key;
        UpdateCurrencySelection();
    }

    private void UpdateCurrencySelection()
    {
        foreach (var (key, card) in _cards)
        {
            var root = (Border)CurrencyGrid.Items[Array.IndexOf(Currency.All, key)];
            root.Background = key == _selectedCurrency
                ? (Brush)FindResource("AccentSoft")
                : (Brush)FindResource("CardBackground");
            root.BorderBrush = key == _selectedCurrency
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("CardBorder");
            root.BorderThickness = key == _selectedCurrency ? new Thickness(2) : new Thickness(1);
        }
    }

    // ── 坐标录制（三态按钮；信号分离坑 #10）──

    private void StartRecordSlot(string slotId, string displayName, Button btn)
    {
        if (string.IsNullOrWhiteSpace(_host.CoordinateHotkey))
        {
            CraftStatusText.Text = "坐标录制热键未绑定，请先在设置中绑定";
            return;
        }
        _host.Coordinates.StartRecording(new CoordinateSlot { SlotId = slotId, DisplayName = displayName });
        btn.Content = $"移动鼠标后按 {_host.CoordinateHotkey}...";
        btn.Background = (Brush)FindResource("AccentSoft");
        btn.BorderBrush = (Brush)FindResource("AccentBrush");
        btn.BorderThickness = new Thickness(1.5);
        btn.Foreground = (Brush)FindResource("AccentBrush");
        btn.FontWeight = FontWeights.Bold;
    }

    private void ClearSlot(string slotId)
    {
        _host.Coordinates.ClearCoordinate(slotId);
        _tool.Coordinates.Remove(slotId);
        UpdateCoordButton(slotId, null);
    }

    private void UpdateAllCoordButtons()
    {
        var coordinates = _host.Coordinates.GetCoordinates();
        foreach (var key in _cards.Keys)
            UpdateCoordButton(key, coordinates.TryGetValue(key, out var pt) ? pt : null);
        UpdateCoordButton("item", coordinates.TryGetValue("item", out var item) ? item : null);
    }

    private void UpdateCoordButton(string slotId, Point? pt)
    {
        if (slotId == "item")
        {
            SetCoordButtonState(ItemCoordButton, ItemClearButton, pt);
        }
        else if (_cards.TryGetValue(slotId, out var card))
        {
            SetCoordButtonState(card.CoordButton, card.ClearButton, pt);
        }
    }

    private void SetCoordButtonState(Button coordBtn, Button clearBtn, Point? pt)
    {
        if (pt is { } p)
        {
            coordBtn.Content = $"✓ ({p.X}, {p.Y})";
            coordBtn.Background = new SolidColorBrush(Color.FromArgb(0x14, 0x1B, 0x8A, 0x3E));
            coordBtn.BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0x1B, 0x8A, 0x3E));
            coordBtn.BorderThickness = new Thickness(1);
            coordBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x3E));
            coordBtn.FontWeight = FontWeights.Normal;
            clearBtn.Visibility = Visibility.Visible;
        }
        else
        {
            coordBtn.Content = "设定坐标";
            coordBtn.Background = (Brush)FindResource("CardBackground");
            coordBtn.BorderBrush = (Brush)FindResource("CardBorder");
            coordBtn.BorderThickness = new Thickness(1);
            coordBtn.Foreground = (Brush)FindResource("TextSecondary");
            coordBtn.FontWeight = FontWeights.Normal;
            clearBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void ItemCoordButton_Click(object sender, RoutedEventArgs e)
    {
        StartRecordSlot("item", "装备位置", ItemCoordButton);
    }

    private void ItemClearButton_Click(object sender, RoutedEventArgs e)
    {
        _host.Coordinates.ClearCoordinate("item");
        _tool.Coordinates.Remove("item");
        UpdateCoordButton("item", null);
    }

    private void ClearAllCoordsButton_Click(object sender, RoutedEventArgs e)
    {
        var ids = _cards.Keys.Append("item").ToArray();
        _host.Coordinates.ClearCoordinates(ids);
        foreach (var key in ids)
        {
            _tool.Coordinates.Remove(key);
            UpdateCoordButton(key, null);
        }
    }

    // ── 词缀池 ──

    private void AddAffixButton_Click(object sender, RoutedEventArgs e)
    {
        var type = (string)((Button)sender).Tag;
        var box = type switch
        {
            "primary" => PrimaryInput,
            "secondary" => SecondaryInput,
            _ => ExcludeInput,
        };
        var text = box.Text.Trim();
        if (text.Length == 0) return;
        if (TryAddAffixTag(type, text))
        {
            box.Clear();
            _dirty = true;
        }
    }

    private void AffixInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        var box = (TextBox)sender;
        var text = box.Text.Trim();
        if (text.Length == 0) return;
        if (TryAddAffixTag((string)box.Tag, text))
        {
            box.Clear();
            _dirty = true;
        }
    }

    private bool TryAddAffixTag(string type, string text)
    {
        var existingPool = FindAffixPool(text);
        if (existingPool is not null)
        {
            var owner = Window.GetWindow(this);
            var message = $"词缀「{text}」已存在于{existingPool}，不能重复加入其他词缀池。";
            if (owner is null)
                MessageBox.Show(message, "配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(owner, message, "配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        AddAffixTag(type, text);
        return true;
    }

    private string? FindAffixPool(string text)
    {
        if (_primaryTags.Any(tag => string.Equals(tag.Text.Trim(), text, StringComparison.Ordinal)))
            return "主词缀池";
        if (_secondaryTags.Any(tag => string.Equals(tag.Text.Trim(), text, StringComparison.Ordinal)))
            return "次级词缀池";
        if (_excludeTags.Any(tag => string.Equals(tag.Text.Trim(), text, StringComparison.Ordinal)))
            return "排除词缀池";
        return null;
    }

    private void AddAffixTag(string type, string text)
    {
        var (bg, border, target) = type switch
        {
            "primary" => ((Color)ColorConverter.ConvertFromString("#E9F7F3"), (Color)ColorConverter.ConvertFromString("#C4E4DB"), _primaryTags),
            "secondary" => ((Color)ColorConverter.ConvertFromString("#EAF5FC"), (Color)ColorConverter.ConvertFromString("#D4E6F0"), _secondaryTags),
            _ => ((Color)ColorConverter.ConvertFromString("#FCF0F2"), (Color)ColorConverter.ConvertFromString("#EED4DA"), _excludeTags),
        };
        var panel = type switch
        {
            "primary" => PrimaryTags,
            "secondary" => SecondaryTags,
            _ => ExcludeTags,
        };

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextPrimary"),
            MaxWidth = 190,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var removeBtn = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var root = new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 2, 6, 2),
            Margin = new Thickness(0, 2, 4, 2),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, removeBtn } },
        };

        var tag = new AffixTagUi { Text = text, Root = root };
        target.Add(tag);
        panel.Children.Add(root);
        removeBtn.MouseLeftButtonUp += (_, _) =>
        {
            target.Remove(tag);
            panel.Children.Remove(root);
            _dirty = true;
        };
    }

    private void ClearPrimaryButton_Click(object sender, RoutedEventArgs e) => ClearPool(_primaryTags, PrimaryTags);
    private void ClearSecondaryButton_Click(object sender, RoutedEventArgs e) => ClearPool(_secondaryTags, SecondaryTags);
    private void ClearExcludeButton_Click(object sender, RoutedEventArgs e) => ClearPool(_excludeTags, ExcludeTags);

    private void ClearPool(List<AffixTagUi> tags, WrapPanel panel)
    {
        tags.Clear();
        panel.Children.Clear();
        _dirty = true;
    }

    // ── 命中数（实时验证，对齐 _on_hit_count_clicked）──

    private void HitCount_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _restoringHitCount) return;
        var radio = (RadioButton)sender;
        var isPrimary = GroupNameOf(radio) == "PrimaryHit";
        var newVal = int.Parse((string)radio.Tag);
        var otherVal = isPrimary ? CurrentSecondaryHit : CurrentPrimaryHit;
        var previousVal = isPrimary ? _lastValidPrimaryHit : _lastValidSecondaryHit;
        var resolvedVal = CraftDecisions.ResolveHitCountSelection(CurrentMode, previousVal, newVal, otherVal);

        if (resolvedVal != newVal)
        {
            var owner = Window.GetWindow(this);
            const string message = "Mode 2 总词缀命中数不能超过 2\nMode 3 总词缀命中数不能超过 3";
            if (owner is null)
                MessageBox.Show(message, "配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(owner, message, "配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);

            _restoringHitCount = true;
            try
            {
                if (isPrimary)
                    SetHitRadio(PrimaryHit0, PrimaryHit1, PrimaryHit2, PrimaryHit3, resolvedVal);
                else
                    SetHitRadio(SecondaryHit0, SecondaryHit1, SecondaryHit2, SecondaryHit3, resolvedVal);
            }
            finally
            {
                _restoringHitCount = false;
            }
            return;
        }

        if (isPrimary)
            _lastValidPrimaryHit = newVal;
        else
            _lastValidSecondaryHit = newVal;
        _dirty = true;
    }

    private static string GroupNameOf(RadioButton r) => r.GroupName;

    private int CurrentPrimaryHit
    {
        get
        {
            foreach (var r in new[] { PrimaryHit0, PrimaryHit1, PrimaryHit2, PrimaryHit3 })
                if (r.IsChecked == true) return int.Parse((string)r.Tag);
            return 0;
        }
    }

    private int CurrentSecondaryHit
    {
        get
        {
            foreach (var r in new[] { SecondaryHit0, SecondaryHit1, SecondaryHit2, SecondaryHit3 })
                if (r.IsChecked == true) return int.Parse((string)r.Tag);
            return 0;
        }
    }

    // ── 模式切换 ──

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _tool.Rules.Mode = CurrentModeFromRadio();
        ApplyModeVisibility();
        // Mode 1 自动选通货
        if (_tool.Rules.Mode == CraftMode.Single &&
            !Currency.ModeCurrencies[CraftMode.Single].Contains(_selectedCurrency))
        {
            _selectedCurrency = Currency.Alteration;
            UpdateCurrencySelection();
        }
    }

    private void Mode2Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _tool.Mode2ScourAlch = Mode2ScourAlch.IsChecked == true;
        _dirty = true;
    }

    private void ExaltCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _tool.UseExalt = ExaltCheck.IsChecked == true;
        _dirty = true;
    }

    private CraftMode CurrentModeFromRadio()
    {
        if (Mode1Radio.IsChecked == true) return CraftMode.Single;
        if (Mode2Radio.IsChecked == true) return CraftMode.AltAug;
        return CraftMode.AltAugRegal;
    }

    // ── 预设 ──

    private void RefreshPresetCombo()
    {
        var current = PresetCombo.SelectedItem as string;
        _refreshingPresets = true;
        try
        {
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("无");
            foreach (var name in _host.Storage.ListPresets()) PresetCombo.Items.Add(name);
        }
        catch (StorageException error)
        {
            NotificationService.ShowConfigurationError(error.Message, Window.GetWindow(this));
        }
        finally
        {
            PresetCombo.SelectedItem = current is not null && PresetCombo.Items.Contains(current) ? current : "无";
            _refreshingPresets = false;
            DeletePresetButton.IsEnabled = PresetCombo.SelectedItem is string selected && selected != "无";
        }
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeletePresetButton is not null)
            DeletePresetButton.IsEnabled = PresetCombo.SelectedItem is string selected && selected != "无";
        if (_refreshingPresets || !IsLoaded || PresetCombo.SelectedItem is not string name || name == "无")
            return;

        // 未保存修改确认（Python _on_preset_selected）
        if (_dirty)
        {
            var reply = MessageBox.Show("当前词缀尚未保存，切换预设将丢失现有词缀。是否继续？",
                "未保存修改", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reply != MessageBoxResult.Yes)
            {
                PresetCombo.SelectionChanged -= PresetCombo_SelectionChanged;
                PresetCombo.SelectedItem = "无";
                PresetCombo.SelectionChanged += PresetCombo_SelectionChanged;
                return;
            }
        }

        JsonObject? data;
        try { data = _host.Storage.LoadPreset(name); }
        catch (StorageException error)
        {
            NotificationService.ShowConfigurationError(error.Message, Window.GetWindow(this));
            PresetCombo.SelectedItem = "无";
            return;
        }
        if (data is not null)
        {
            ApplyPreset(data);
            _dirty = false;
        }
        else
        {
            MessageBox.Show(Window.GetWindow(this), $"预设「{name}」已不存在，当前词缀设置保留。",
                "预设不存在", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshPresetCombo();
        }
    }

    private void ApplyPreset(JsonObject data)
    {
        ClearPool(_primaryTags, PrimaryTags);
        ClearPool(_secondaryTags, SecondaryTags);
        ClearPool(_excludeTags, ExcludeTags);

        foreach (var text in ReadStringArray(data, "primary_affixes")) AddAffixTag("primary", text);
        foreach (var text in ReadStringArray(data, "secondary_affixes")) AddAffixTag("secondary", text);
        ApplyHitCounts(
            ReadInt(data, "primary_hit_count", 0),
            ReadInt(data, "secondary_hit_count", 0));
        foreach (var text in ReadStringArray(data, "exclude_affixes")) AddAffixTag("exclude", text);
    }

    private static IEnumerable<string> ReadStringArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray arr) yield break;
        foreach (var item in arr)
        {
            var text = item switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonObject o when o["text"] is JsonValue v && v.TryGetValue<string>(out var s) => s,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }

    private static int ReadInt(JsonObject obj, string key, int fallback)
        => obj[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    private static void SetHitRadio(RadioButton r0, RadioButton r1, RadioButton r2, RadioButton r3, int value)
        => (value switch { 0 => r0, 1 => r1, 2 => r2, _ => r3 }).IsChecked = true;

    private void ApplyHitCounts(int primaryHitCount, int secondaryHitCount)
    {
        _restoringHitCount = true;
        try
        {
            SetHitRadio(PrimaryHit0, PrimaryHit1, PrimaryHit2, PrimaryHit3, primaryHitCount);
            SetHitRadio(SecondaryHit0, SecondaryHit1, SecondaryHit2, SecondaryHit3, secondaryHitCount);
            _lastValidPrimaryHit = CurrentPrimaryHit;
            _lastValidSecondaryHit = CurrentSecondaryHit;
        }
        finally
        {
            _restoringHitCount = false;
        }
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("保存预设", "请输入预设名称：");
        var owner = Window.GetWindow(this);
        if (owner is not null) dialog.Owner = owner;
        var name = dialog.GetName();
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        if (!StorageService.TryValidatePresetName(name, out var validationError))
        {
            if (owner is not null)
                MessageBox.Show(owner, validationError, "预设名称无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(validationError, "预设名称无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_host.Storage.ListPresets().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var reply = MessageBox.Show($"已存在同名预设「{name}」，是否覆盖？",
                "覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reply != MessageBoxResult.Yes) return;
        }

        var data = new JsonObject
        {
            ["primary_affixes"] = new JsonArray(_primaryTags.Select(t => (JsonNode)t.Text).ToArray()),
            ["primary_hit_count"] = CurrentPrimaryHit,
            ["secondary_affixes"] = new JsonArray(_secondaryTags.Select(t => (JsonNode)t.Text).ToArray()),
            ["secondary_hit_count"] = CurrentSecondaryHit,
            ["exclude_affixes"] = new JsonArray(_excludeTags.Select(t => (JsonNode)t.Text).ToArray()),
        };
        _host.Storage.SavePreset(name, data);
        _dirty = false;
        RefreshPresetCombo();
        PresetCombo.SelectedItem = name;
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not string name || name == "无") return;
        var owner = Window.GetWindow(this);
        var reply = MessageBox.Show(owner, $"确定删除预设「{name}」？\n\n当前词缀设置会保留。",
            "删除预设", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;
        try
        {
            _host.Storage.DeletePreset(name);
            RefreshPresetCombo();
        }
        catch (StorageException error) { NotificationService.ShowConfigurationError(error.Message, owner); }
    }

    private void OpenPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_host.Storage.DataDir, "presets"))
            {
                UseShellExecute = true,
            });
        }
        catch (Exception) { }
    }

    // ── 启停（快捷键 F5/F6 触发；无按钮，对齐用户反馈）──

    /// <summary>
    /// 收集全部页面状态并保存。F5、切换抽屉和退出共用此入口，
    /// 避免规则、模式与 Craft 设置分别处于不同版本。
    /// </summary>
    public void CollectRulesFromUi()
    {
        // 窗口首帧前关闭时 Loaded 尚未把已有规则填入控件，不能用空标签/默认值覆盖。
        if (!_initialized) return;
        UpdateRulesFromUi();
        _tool.SaveRulesToStorage();
        _tool.SaveSettingsToStorage();
    }

    /// <summary>收集 UI → Rules（对齐 Python update_rules）。</summary>
    private void UpdateRulesFromUi()
    {
        _tool.Rules.Mode = CurrentModeFromRadio();
        _tool.Rules.SingleCurrency = _selectedCurrency;
        _tool.Rules.PrimaryHitCount = CurrentPrimaryHit;
        _tool.Rules.SecondaryHitCount = CurrentSecondaryHit;
        _tool.Rules.PrimaryAffixes.Clear();
        _tool.Rules.PrimaryAffixes.AddRange(_primaryTags.Select(t => new AffixRule { Text = t.Text }));
        _tool.Rules.SecondaryAffixes.Clear();
        _tool.Rules.SecondaryAffixes.AddRange(_secondaryTags.Select(t => new AffixRule { Text = t.Text }));
        _tool.Rules.ExcludeAffixes.Clear();
        _tool.Rules.ExcludeAffixes.AddRange(_excludeTags.Select(t => new AffixRule { Text = t.Text }));
        _tool.Mode2ScourAlch = Mode2ScourAlch.IsChecked == true;
        _tool.UseExalt = ExaltCheck.IsChecked == true;
        _tool.DelayMs = (int)DelaySlider.Value;
    }

    // ── 引擎事件（后台线程 → Dispatcher）──

    private void SubscribeEngine()
    {
        var engine = _tool.Engine;
        if (engine is null) return;

        engine.StatusUpdated += s => Dispatcher.BeginInvoke(() =>
        {
            CraftStatusText.Text = _tool.IsEnabled ? s.Text : "功能已停用";
        });
        engine.Stopped += reason => Dispatcher.BeginInvoke(() =>
        {
            CraftStatusText.Text = _tool.IsEnabled ? reason : "功能已停用";
        });
        // 错误弹窗由 CraftTool 全局订阅（页面未开也弹），这里不重复订阅
    }

    // ── 加载 UI 状态（对齐 _load_state）──

    private void LoadStateIntoUi()
    {
        // 子模式选项只在首次加载时从持久状态恢复。切换大模式仅改变可见性，
        // 不得用旧值覆盖用户刚刚作出的、尚未落盘的选择。
        Mode2AltAug.IsChecked = !_tool.Mode2ScourAlch;
        Mode2ScourAlch.IsChecked = _tool.Mode2ScourAlch;
        ExaltCheck.IsChecked = _tool.UseExalt;

        // 模式
        switch (_tool.Rules.Mode)
        {
            case CraftMode.Single: Mode1Radio.IsChecked = true; break;
            case CraftMode.AltAug: Mode2Radio.IsChecked = true; break;
            default: Mode3Radio.IsChecked = true; break;
        }
        ApplyModeVisibility();

        // 通货选择
        _selectedCurrency = _tool.Rules.SingleCurrency;
        UpdateCurrencySelection();

        // 词缀
        ApplyHitCounts(_tool.Rules.PrimaryHitCount, _tool.Rules.SecondaryHitCount);
        foreach (var r in _tool.Rules.PrimaryAffixes) AddAffixTag("primary", r.Text);
        foreach (var r in _tool.Rules.SecondaryAffixes) AddAffixTag("secondary", r.Text);
        foreach (var r in _tool.Rules.ExcludeAffixes) AddAffixTag("exclude", r.Text);

        _dirty = false;
    }
}

/// <summary>极简输入对话框（预设保存用，替代 Python QInputDialog）。</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _input = new() { Width = 240, Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(4, 2, 4, 2) };

    public InputDialog(string title, string prompt)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "取消", Width = 70, IsCancel = true };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = prompt, FontSize = 12 },
                _input,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        _input.Focus();
    }

    /// <summary>模态输入，返回文本（取消/空返回 null）。</summary>
    public string? GetName()
    {
        base.ShowDialog();
        var text = _input.Text.Trim();
        return DialogResult == true ? text : null;
    }
}

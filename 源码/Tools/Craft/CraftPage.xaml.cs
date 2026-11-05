using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShiKe.Host;
using ShiKe.Services;
using ShiKe.Presentation;

namespace ShiKe.Tools.Craft;

/// <summary>
/// 洗装页面（对齐 Python config_tab.py 全部交互逻辑）。
/// 三栏：模式与延迟 / 通货网格(2列,按模式显示子集)与坐标 / 词缀及预设。
/// 三态坐标录制（信号分离坑#10）/ 单通货固定一命中、多通货启动校验 / 预设管理 / 启停。
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
    private readonly LatestPageState<string> _statusPresentation;
    private readonly Dictionary<string, CurrencyCardUi> _cards = [];
    private readonly List<AffixTagUi> _primaryTags = [];
    private readonly List<AffixTagUi> _secondaryTags = [];
    private readonly List<AffixTagUi> _excludeTags = [];
    private bool _refreshingPresets;

    private string _selectedCurrency = Currency.Alteration;
    private bool _dirty;
    private bool _initialized;   // Loaded 防重复（WPF Loaded 在每次进入可视树时触发，卡片会翻倍）
    private bool _loadingState;
    private bool _restoringHitCount;
    private bool _refreshingEnabled;

    public CraftPage(ToolHost host, CraftTool tool)
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/拾刻;component/Tools/Craft/CraftComponentResources.xaml", UriKind.Relative),
        });
        _host = host;
        _tool = tool;
        _statusPresentation = new LatestPageState<string>(this,
            text => CraftStatusText.Text = _tool.IsEnabled ? text : "功能已停用");
        // 在模板首次应用前设置开关，Loaded 仍负责较重的卡片/规则初始化。
        RefreshEnabledPresentation();
        ExaltCheck.IsChecked = tool.UseExalt;
        RefreshHotkeyHints(tool.HotkeyStart, tool.HotkeyStop, host.CoordinateHotkey);

        Loaded += (_, _) => OnLoaded();
    }

    /// <summary>由 SettingsTool 在热键成功重注册后调用；不收集或改写页面业务配置。</summary>
    internal void RefreshHotkeyHints(string startHotkey, string stopHotkey, string coordinateHotkey)
    {
        CraftHotkeyHint.Text = $"{HotkeySetting.Display(startHotkey)} 启动 · {HotkeySetting.Display(stopHotkey)} 停止 · {HotkeySetting.Display(coordinateHotkey)} 录坐标";
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
        DelaySlider.Value = _tool.DelayMs;
        DelayValue.Text = $"{_tool.DelayMs} ms";
        DelaySlider.ValueChanged += (_, _) =>
        {
            DelayValue.Text = $"{(int)DelaySlider.Value} ms";
            SynchronizeEditedRules();
        };
        RefreshEnabledPresentation();
        // 恢复时控件可能采用已有的币种/数量后备值；运行配置必须与首次显示保持一致。
        // 这里只同步内存，不改变原来在启动、切页或退出时才写盘的时机。
        SynchronizeEditedRules();
        _tool.MarkEditorReady();
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
            ThemedMessageBox.Show(message, title, MessageBoxButton.OK, image);
        else
            ThemedMessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
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
                Style = (Style)FindResource("CraftCoordinateClear"),
                Visibility = Visibility.Collapsed,
            };

            var nameLabel = new TextBlock
            {
                Text = Currency.Label(key),
                Style = (Style)FindResource("CraftCurrencyLabel"),
            };

            var coordRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            coordRow.ColumnDefinitions.Add(new ColumnDefinition());
            coordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(clearBtn, 1);
            coordRow.Children.Add(coordBtn);
            coordRow.Children.Add(clearBtn);

            var root = new Border
            {
                Style = (Style)FindResource("CraftCurrencyCard"),
                Tag = key,
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
        SingleCurrencyFrame.Visibility = CurrentMode == CraftMode.Single ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SingleCurrency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingState || SingleCurrencyCombo.SelectedValue is not string key) return;
        _selectedCurrency = key;
        _dirty = true;
        SynchronizeEditedRules();
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
            coordBtn.Background = (Brush)FindResource("CraftCoordinateBackground");
            coordBtn.BorderBrush = (Brush)FindResource("CraftCoordinateBorder");
            coordBtn.BorderThickness = new Thickness(1);
            coordBtn.Foreground = (Brush)FindResource("CraftCoordinateForeground");
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
                ThemedMessageBox.Show(message, "配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                ThemedMessageBox.Show(owner, message, "配置提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        AddAffixTag(type, text);
        SynchronizeEditedRules();
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
        var (resourcePrefix, target) = type switch
        {
            "primary" => ("CraftPrimaryTag", _primaryTags),
            "secondary" => ("CraftSecondaryTag", _secondaryTags),
            _ => ("CraftExcludeTag", _excludeTags),
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
            Style = (Style)FindResource("CraftAffixLabel"),
        };
        var removeBtn = new TextBlock
        {
            Text = "✕",
            Style = (Style)FindResource("CraftAffixRemove"),
        };
        var root = new Border
        {
            Style = (Style)FindResource("CraftAffixTag"),
            Background = (Brush)FindResource(resourcePrefix + "Background"),
            BorderBrush = (Brush)FindResource(resourcePrefix + "Border"),
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
            SynchronizeEditedRules();
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
        SynchronizeEditedRules();
    }

    // ── 命中数：编辑时保留选择，Mode2/3 上限统一在启动前校验 ──

    private void HitCount_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingState || _restoringHitCount) return;
        _dirty = true;
        SynchronizeEditedRules();
    }

    private void RefreshHitCountPresentation()
    {
        var single = CurrentMode == CraftMode.Single;
        SingleHitHint.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        PrimaryHitControls.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        SecondaryHitControls.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        SecondaryPoolSection.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
    }

    private int CurrentPrimaryHit
    {
        get
        {
            if (CurrentMode == CraftMode.Single) return 1;
            foreach (var r in new[] { PrimaryHit0, PrimaryHit1, PrimaryHit2, PrimaryHit3 })
                if (r.IsChecked == true) return int.Parse((string)r.Tag);
            return 0;
        }
    }

    private int CurrentSecondaryHit
    {
        get
        {
            if (CurrentMode == CraftMode.Single) return 0;
            foreach (var r in new[] { SecondaryHit0, SecondaryHit1, SecondaryHit2, SecondaryHit3 })
                if (r.IsChecked == true) return int.Parse((string)r.Tag);
            return 0;
        }
    }

    // ── 模式切换 ──

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingState) return;
        _tool.Rules.Mode = CurrentModeFromRadio();
        ApplyModeVisibility();
        RefreshHitCountPresentation();
        _dirty = true;
        SynchronizeEditedRules();
    }

    private void Mode2Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingState) return;
        _tool.Mode2ScourAlch = Mode2ScourAlch.IsChecked == true;
        _dirty = true;
        SynchronizeEditedRules();
    }

    private void ExaltCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingState) return;
        _tool.UseExalt = ExaltCheck.IsChecked == true;
        _dirty = true;
        SynchronizeEditedRules();
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
            foreach (var name in _tool.ListPresets()) PresetCombo.Items.Add(name);
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
            var reply = ThemedMessageBox.Show("当前词缀尚未保存，切换预设将丢失现有词缀。是否继续？",
                "未保存修改", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reply != MessageBoxResult.Yes)
            {
                PresetCombo.SelectionChanged -= PresetCombo_SelectionChanged;
                PresetCombo.SelectedItem = "无";
                PresetCombo.SelectionChanged += PresetCombo_SelectionChanged;
                return;
            }
        }

        CraftPreset? data;
        try { data = _tool.LoadPreset(name); }
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
            ThemedMessageBox.Show(Window.GetWindow(this), $"预设「{name}」已不存在，当前词缀设置保留。",
                "预设不存在", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshPresetCombo();
        }
    }

    private void ApplyPreset(CraftPreset data)
    {
        _loadingState = true;
        try
        {
            ClearPool(_primaryTags, PrimaryTags);
            ClearPool(_secondaryTags, SecondaryTags);
            ClearPool(_excludeTags, ExcludeTags);

            foreach (var text in data.PrimaryAffixes) AddAffixTag("primary", text);
            foreach (var text in data.SecondaryAffixes) AddAffixTag("secondary", text);
            ApplyHitCounts(data.PrimaryHitCount, data.SecondaryHitCount);
            foreach (var text in data.ExcludeAffixes) AddAffixTag("exclude", text);
        }
        finally { _loadingState = false; }
        RefreshHitCountPresentation();
        SynchronizeEditedRules();
    }

    private static void SetHitRadio(RadioButton r0, RadioButton r1, RadioButton r2, RadioButton r3, int value)
        => (value switch { 0 => r0, 1 => r1, 2 => r2, _ => r3 }).IsChecked = true;

    private void ApplyHitCounts(int primaryHitCount, int secondaryHitCount)
    {
        _restoringHitCount = true;
        try
        {
            SetHitRadio(PrimaryHit0, PrimaryHit1, PrimaryHit2, PrimaryHit3, primaryHitCount);
            SetHitRadio(SecondaryHit0, SecondaryHit1, SecondaryHit2, SecondaryHit3, secondaryHitCount);
        }
        finally
        {
            _restoringHitCount = false;
        }
        if (!_loadingState) RefreshHitCountPresentation();
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
                ThemedMessageBox.Show(owner, validationError, "预设名称无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                ThemedMessageBox.Show(validationError, "预设名称无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_tool.ListPresets().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var reply = ThemedMessageBox.Show($"已存在同名预设「{name}」，是否覆盖？",
                "覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (reply != MessageBoxResult.Yes) return;
        }

        _tool.SavePreset(name);
        _dirty = false;
        RefreshPresetCombo();
        PresetCombo.SelectedItem = name;
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not string name || name == "无") return;
        var owner = Window.GetWindow(this);
        var reply = ThemedMessageBox.Show(owner, $"确定删除预设「{name}」？\n\n当前词缀设置会保留。",
            "删除预设", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;
        try
        {
            _tool.DeletePreset(name);
            RefreshPresetCombo();
        }
        catch (StorageException error) { NotificationService.ShowConfigurationError(error.Message, owner); }
    }

    private void OpenPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_tool.PresetDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception) { }
    }

    // ── 启停（快捷键 F5/F6 触发；无按钮，对齐用户反馈）──

    /// <summary>
    /// 保留给旧页面验证的显式提交入口；正常编辑已即时同步，工具运行不再调用页面。
    /// </summary>
    public void CollectRulesFromUi()
    {
        // 窗口首帧前关闭时 Loaded 尚未把已有规则填入控件，不能用空标签/默认值覆盖。
        if (!_initialized) return;
        UpdateRulesFromUi();
        _tool.SaveEditedState();
    }

    private void SynchronizeEditedRules()
    {
        if (!_initialized || _loadingState) return;
        UpdateRulesFromUi();
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

        void OnStatus(CraftStatus status) => PublishStatus(status.Text);
        engine.StatusUpdated += OnStatus;
        engine.Stopped += PublishStatus;
        _statusPresentation.Disposed += () =>
        {
            engine.StatusUpdated -= OnStatus;
            engine.Stopped -= PublishStatus;
        };
        PublishStatus(_host.Statuses.Read(_tool.Id).Selected?.Text ?? "就绪");
        // 错误弹窗由 CraftTool 全局订阅（页面未开也弹），这里不重复订阅
    }

    internal void PublishStatus(string text) => _statusPresentation.Publish(text);
    internal int StatusRenderCount => _statusPresentation.RenderedCount;
    internal int StatusPostCount => _statusPresentation.PostedCount;

    // ── 加载 UI 状态（对齐 _load_state）──

    private void LoadStateIntoUi()
    {
        _loadingState = true;
        try
        {
            // 子模式选项只在首次加载时从持久状态恢复，切模式不覆盖尚未落盘的选择。
            Mode2AltAug.IsChecked = !_tool.Mode2ScourAlch;
            Mode2ScourAlch.IsChecked = _tool.Mode2ScourAlch;
            ExaltCheck.IsChecked = _tool.UseExalt;

            switch (_tool.Rules.Mode)
            {
                case CraftMode.Single: Mode1Radio.IsChecked = true; break;
                case CraftMode.AltAug: Mode2Radio.IsChecked = true; break;
                default: Mode3Radio.IsChecked = true; break;
            }
            ApplyModeVisibility();

            _selectedCurrency = Currency.ModeCurrencies[CraftMode.Single].Contains(_tool.Rules.SingleCurrency)
                ? _tool.Rules.SingleCurrency : Currency.Alteration;
            SingleCurrencyCombo.SelectedValue = _selectedCurrency;

            ApplyHitCounts(_tool.Rules.PrimaryHitCount, _tool.Rules.SecondaryHitCount);
            foreach (var r in _tool.Rules.PrimaryAffixes) AddAffixTag("primary", r.Text);
            foreach (var r in _tool.Rules.SecondaryAffixes) AddAffixTag("secondary", r.Text);
            foreach (var r in _tool.Rules.ExcludeAffixes) AddAffixTag("exclude", r.Text);
        }
        finally { _loadingState = false; }
        RefreshHitCountPresentation();
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

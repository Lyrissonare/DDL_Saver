using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DDLSaver.Core;
using DDLSaver.Models;
using DDLSaver.Services;
using Microsoft.Win32;

namespace DDLSaver.Windows;

/// <summary>
/// 主窗口（规格 3.2）：任务列表（按紧急度排序）+ 新建/编辑/删除/完成 + 统计页 + 设置页。
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly App _app;
    private bool _settingsReady;      // 防止初始化期间的事件回写
    private Guid? _selectedTaskId;

    // 位置下拉框的分组文案
    private static readonly string[] DockNames = { "顶部居中", "底部居中", "自由拖拽" };
    private static readonly FloatDock[] DockValues = { FloatDock.Top, FloatDock.Bottom, FloatDock.Free };

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;
        Icon = app.CreateAppIcon();
        SourceInitialized += (_, _) => ThemeService.ApplyWindowBackground(this);

        // 组合框初始数据
        for (var i = 1; i <= 5; i++)
            CountCombo.Items.Add(i);
        foreach (var name in DockNames)
            DockCombo.Items.Add(name);

        foreach (var h in new[] { 2, 3, 4, 6 })
            FreqHoursCombo.Items.Add($"{h} 小时");
        FreqHoursCombo.SelectedIndex = 1;

        LoadSettingsToUi();
        _settingsReady = true;
    }

    public void ShowAndActivate()
    {
        // 防御：显示器被拔出等场景可能把窗口推到屏外，显示前钳回主屏工作区
        var vs = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var isOffscreen = Left + Width < vs.Left || Left > vs.Right || Top + Height < vs.Top || Top > vs.Bottom;
        if (isOffscreen)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Left = vs.Left + (vs.Width - Width) / 2;
            Top = vs.Top + (vs.Height - Height) / 2;
        }

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        RefreshList();
    }

    /// <summary>打开「新建任务」对话框（--open-edit 调试启动开关也走这里）。</summary>
    public void OpenNewTaskDialog()
        => OpenEditDialog(null);

    /// <summary>打开主窗口并定位到指定任务（Toast 点击 / 早报点击）。</summary>
    public void LocateTask(Guid id)
    {
        ShowAndActivate();
        var row = _rows.FirstOrDefault(r => r.Task.Id == id);
        if (row is null)
            return;
        TaskList.SelectedItem = row;
        var container = TaskList.ItemContainerGenerator.ContainerFromItem(row) as ListViewItem;
        container?.BringIntoView();
    }

    public void SetStatus(string message)
    {
        StatusText.Text = $"{DateTime.Now:HH:mm:ss}　{message}";
        SaveTimeText.Text = $"数据保存：{DateTime.Now:HH:mm:ss}";
    }

    #region 列表刷新

    private List<TaskRowVm> _rows = new();

    public void RefreshList()
    {
        var ordered = _app.Store.Tasks
            .OrderBy(t => t, new UrgencyComparer())
            .ToList();

        _rows = ordered.Select(t => new TaskRowVm(t)).ToList();
        if (_selectedTaskId is Guid sel)
        {
            var match = _rows.FirstOrDefault(r => r.Task.Id == sel);
            if (match is not null)
                TaskList.SelectedItem = match;
        }
        TaskList.ItemsSource = _rows;
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var task = SelectedRow()?.Task;
        ToggleCompleteButton.Content = task switch
        {
            null => "✓ 标记完成",
            _ when task.IsCompleted => "↩ 恢复",
            _ when task.HasProgress && task.DoneUnits < task.TotalUnits!.Value => "✓ 完成 1 次",
            _ => "✓ 标记完成",
        };
    }

    private TaskRowVm? SelectedRow()
        => TaskList.SelectedItem as TaskRowVm;

    public void RefreshStats()
    {
        var stats = StatsCalculator.Compute(_app.Store);

        StatCards.Children.Clear();
        AddStatCard("连续完成", $"{stats.StreakDays} 天");
        AddStatCard("本周完成", $"{stats.CompletedThisWeek} 个");
        AddStatCard("本周到期", $"{stats.DueThisWeek} 个");
        AddStatCard("累计完成", $"{stats.TotalCompleted} 个");
        AddStatCard("当前逾期", $"{stats.OverdueCount} 个",
            stats.OverdueCount > 0 ? new SolidColorBrush(Color.FromRgb(0xB7, 0x1C, 0x1C)) : null);

        RecentList.ItemsSource = stats.RecentCompletions.Select(c => new RecentItemVm
        {
            WhenText = c.CompletedAt.ToString("M月d日 HH:mm"),
            Title = c.Title,
        }).ToList();
    }

    private void AddStatCard(string label, string value, Brush? accent = null)
    {
        var text = new TextBlock
        {
            Text = value,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = accent ?? ThemeService.Get("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var head = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = ThemeService.Get("TextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };
        var card = new Border
        {
            Background = ThemeService.Get("CardBgBrush"),
            BorderBrush = ThemeService.Get("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 12, 8, 10),
            Margin = new Thickness(0, 0, 8, 0),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            Child = new StackPanel { Children = { head, text } },
        };
        StatCards.Children.Add(card);
    }

    #endregion

    #region 任务操作

    private DdlTask? PickTask()
    {
        var row = SelectedRow();
        if (row is null)
        {
            SetStatus("请先选中一行任务");
            return null;
        }
        return row.Task;
    }

    private void OnNewTask(object sender, RoutedEventArgs e)
        => OpenEditDialog(null);

    private void OnQuickAdd(object sender, RoutedEventArgs e)
        => _app.ShowQuickAdd();

    private void OnEditTask(object sender, RoutedEventArgs e)
    {
        var task = PickTask();
        if (task is null) return;
        OpenEditDialog(task);
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var task = PickTask();
        if (task is null) return;
        OpenEditDialog(task);
    }

    private void OpenEditDialog(DdlTask? task)
    {
        var dlg = new TaskEditWindow(_app.Store, task, _app.SettingsService.Settings.DefaultReminderPlan)
        {
            Owner = this,
        };
        dlg.ShowDialog();
        // 编辑窗口内部保存后触发 NotifyChanged；这里仅刷新统计兜底
        RefreshStats();
    }

    /// <summary>完成 / 恢复切换（含重复任务自动生成下一周期实例）。</summary>
    private void OnToggleComplete(object sender, RoutedEventArgs e)
    {
        var btn = sender as FrameworkElement;
        var task = (btn?.Tag as DdlTask) ?? PickTask();
        if (task is null) return;
        ToggleComplete(task, btn);
    }

    private void OnMenuToggleComplete(object sender, RoutedEventArgs e)
    {
        var task = PickTask();
        if (task is null) return;
        ToggleComplete(task, null);
    }

    private void ToggleComplete(DdlTask task, FrameworkElement? trigger)
    {
        // 恢复未完成
        if (task.IsCompleted)
        {
            task.CompletedAt = null;
            _app.Store.NotifyChanged();
            SetStatus($"已恢复「{task.Title}」为未完成");
            RefreshList();
            return;
        }

        _app.Store.AddCompletion(task);

        // 总量任务（如校园跑）未计满：每次点击只完成 1 次，计满总量才整体完成
        if (task.HasProgress && task.DoneUnits < task.TotalUnits!.Value)
        {
            task.DoneUnits++;
            if (task.DoneUnits < task.TotalUnits!.Value)
            {
                AnimateCompletion(trigger);
                _app.Store.NotifyChanged();
                SetStatus($"「{task.Title}」完成 1 {task.UnitText}：进度 {task.DoneUnits}/{task.TotalUnits}，还需 {task.RemainingUnits} {task.UnitText}");
                RefreshList();
                return;
            }
            // 刚好计满 → 继续走下方整体完成逻辑
        }

        if (task.Recurrence != Recurrence.None)
        {
            // 重复任务：生成下一周期实例（顺延周期、进度清零、子任务复位、提醒记录清空）
            var next = task.CloneBase();
            next.Deadline = AdvancePeriod(task.Deadline, task.Recurrence);
            next.DoneUnits = 0;
            next.SubTasks.ForEach(s => s.IsDone = false);
            next.LastNotified.Clear();
            next.CompletedAt = null;
            next.CreatedAt = DateTime.Now;

            var idx = _app.Store.Tasks.IndexOf(task);
            if (idx >= 0) _app.Store.Tasks[idx] = next;

            // 完成动效
            AnimateCompletion(trigger);
            _app.Store.NotifyChanged();
            SetStatus($"已完成「{task.Title}」；已生成下一周期实例：{next.Deadline:MM月dd日 HH:mm}");
        }
        else
        {
            task.CompletedAt = DateTime.Now;
            AnimateCompletion(trigger);
            _app.Store.NotifyChanged();
            SetStatus($"已完成「{task.Title}」");
        }
        RefreshList();
    }

    private static DateTime AdvancePeriod(DateTime deadline, Recurrence recurrence) => recurrence switch
    {
        Recurrence.Weekly => deadline.AddDays(7),
        Recurrence.Biweekly => deadline.AddDays(14),
        Recurrence.Monthly => deadline.AddMonths(1),
        _ => deadline,
    };

    private void OnDeleteTask(object sender, RoutedEventArgs e)
    {
        var task = PickTask();
        if (task is null) return;
        ConfirmDelete(task);
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        var task = PickTask();
        if (task is null) return;
        ConfirmDelete(task);
    }

    private void ConfirmDelete(DdlTask task)
    {
        var answer = MessageBox.Show(this,
            $"确定删除「{task.Title}」吗？\n删除后不可恢复。",
            "删除任务", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        _app.Store.Tasks.Remove(task);
        _selectedTaskId = null;
        _app.Store.NotifyChanged();
        SetStatus($"已删除「{task.Title}」");
        RefreshList();
    }

    private void OnMenuEdit(object sender, RoutedEventArgs e)
    {
        var task = PickTask();
        if (task is null) return;
        OpenEditDialog(task);
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl tc && tc.SelectedIndex == 1)
            RefreshStats();
    }

    /// <summary>完成动画：绿色对勾缩放 + 淡出（克制的 700ms）。</summary>
    private void AnimateCompletion(FrameworkElement? trigger)
    {
        if (trigger is null)
            return;
        try
        {
            var lvItem = FindVisualParent<ListViewItem>(trigger);
            if (lvItem is null)
                return;

            var center = lvItem.TransformToAncestor(TaskList).Transform(new Point(lvItem.ActualWidth / 2, lvItem.ActualHeight / 2));

            var layer = AdornerLayer.GetAdornerLayer(TaskList);
            if (layer is null)
                return;

            var check = new CheckmarkAdorner(TaskList, center);
            layer.Add(check);
            check.RunThenRemove(layer);
        }
        catch
        {
            // 动画失败不影响功能
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var cur = child;
        while (cur is not null)
        {
            if (cur is T hit) return hit;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    #endregion

    #region 导入导出

    private void OnImportExportMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"DDLSaver-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ImportExportService.ExportJson(dlg.FileName, _app.Store);
            SetStatus($"已导出 {_app.Store.Tasks.Count} 个任务到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"DDLSaver-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ImportExportService.ExportCsv(dlg.FileName, _app.Store);
            SetStatus($"已导出 CSV 到 {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportJson(object sender, RoutedEventArgs e)
        => ImportJson(replace: false);

    private void OnImportJsonMerge(object sender, RoutedEventArgs e)
        => ImportJson(replace: false);

    private void OnImportJsonReplace(object sender, RoutedEventArgs e)
        => ImportJson(replace: true);

    private void ImportJson(bool replace)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;

        if (replace)
        {
            var answer = MessageBox.Show(this,
                "替换导入将清空当前所有任务与完成记录（会自动生成备份），确定继续吗？",
                "替换导入", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        try
        {
            var (tasks, completions) = ImportExportService.ImportJson(dlg.FileName, _app.Store, replace);
            SetStatus($"已导入 {tasks} 个任务（{completions} 条完成记录）");
            MessageBox.Show(this, $"导入完成：{tasks} 个任务。", "导入", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshList();
            RefreshStats();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}", "导入", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportIcs(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入 .ics 日历",
            Filter = "iCalendar 文件 (*.ics)|*.ics",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var tasks = ImportExportService.ParseIcs(dlg.FileName);
            if (tasks.Count == 0)
            {
                MessageBox.Show(this, "没有找到可导入的 VEVENT 条目。", "导入 .ics",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var t in tasks)
                _app.Store.Tasks.Add(t);
            _app.Store.NotifyChanged();
            SetStatus($"已从 .ics 导入 {tasks.Count} 个任务");
            MessageBox.Show(this, $"已导入 {tasks.Count} 个任务（分类：杂事，可在编辑中调整）。", "导入 .ics",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}", "导入 .ics", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnShowMorningReport(object sender, RoutedEventArgs e)
        => _app.ShowMorningReport();

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _app.Store.DataDir,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 忽略
        }
    }

    #endregion

    #region 设置页

    /// <summary>把设置写入控件（期间抑制事件回调）。</summary>
    private void LoadSettingsToUi()
    {
        var s = _app.SettingsService.Settings;
        _settingsReady = false;

        OpacitySlider.Value = Math.Clamp(s.FloatOpacity, 0.3, 1.0);
        OpacityLabel.Text = $"{(int)Math.Round(OpacitySlider.Value * 100)}%";
        FontSizeSlider.Value = Math.Clamp(s.FloatFontSize, 10, 28);
        FontSizeLabel.Text = $"{FontSizeSlider.Value:0}px";
        CountCombo.SelectedIndex = Math.Clamp(s.FloatCount, 1, 5) - 1;
        DockCombo.SelectedIndex = Math.Max(0, Array.IndexOf(DockValues, s.FloatDock));

        MouseThroughCheck.IsChecked = s.MouseThrough;
        FullscreenCheck.IsChecked = s.FullscreenAutoHide;
        ApplyModeRadios(s.VisibilityMode);
        ModeHintText.Text = UrgencyHelper.HasUrgentPending(_app.Store.Tasks)
            ? "⚠ 当前有 24 小时内到期任务，不能完全隐藏。"
            : "完全隐藏仅允许在没有任何 24 小时内到期任务时使用。";

        // 浮窗独立透明度
        BgOpacitySlider.Value = Math.Clamp(s.FloatBackgroundOpacity, 0.0, 1.0);
        BgOpacityLabel.Text = $"{(int)Math.Round(BgOpacitySlider.Value * 100)}%";
        TextOpacitySlider.Value = Math.Clamp(s.FloatTextOpacity, 0.3, 1.0);
        TextOpacityLabel.Text = $"{(int)Math.Round(TextOpacitySlider.Value * 100)}%";

        // 主题
        ThemeLightRadio.IsChecked = s.ThemeMode == AppTheme.Light;
        ThemeDarkRadio.IsChecked = s.ThemeMode == AppTheme.Dark;

        // 窗口背景
        BackdropMicaRadio.IsChecked = s.Background == WindowBackground.Mica;
        BackdropAcrylicRadio.IsChecked = s.Background == WindowBackground.Acrylic;
        BackdropSolidRadio.IsChecked = s.Background == WindowBackground.Solid;

        var plan = s.DefaultReminderPlan;
        Rem7dCheck.IsChecked = plan.Enable7d;
        RemDailyCheck.IsChecked = plan.Enable3dDaily;
        Rem24hCheck.IsChecked = plan.Enable24hFrequent;
        DailyTimeBox.Text = plan.DailyTime;
        FreqHoursCombo.SelectedIndex = Math.Max(0, new[] { 2, 3, 4, 6 }.ToList().IndexOf(plan.FreqHours));

        HotkeyEnabledCheck.IsChecked = s.HotkeyEnabled;
        HotkeyBox.Text = FormatHotkey(s.HotkeyModifiers, s.HotkeyKey);

        AutoStartCheck.IsChecked = s.AutoStart;
        MorningCheck.IsChecked = s.MorningReportEnabled;

        _settingsReady = true;
    }

    private void ApplyModeRadios(VisibilityMode mode)
    {
        ModeFullRadio.IsChecked = mode == VisibilityMode.Full;
        ModeMiniRadio.IsChecked = mode == VisibilityMode.Mini;
        ModeHiddenRadio.IsChecked = mode == VisibilityMode.Hidden;
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsReady) return;
        _app.SettingsService.Settings.FloatOpacity = Math.Clamp(e.NewValue, 0.3, 1.0);
        OpacityLabel.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        _app.SettingsService.Save();
        _app.Float?.ApplySettings();
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsReady) return;
        _app.SettingsService.Settings.FloatFontSize = Math.Clamp(e.NewValue, 10, 28);
        FontSizeLabel.Text = $"{e.NewValue:0}px";
        _app.SettingsService.Save();
        _app.Float?.ApplySettings();
    }

    private void OnCountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || CountCombo.SelectedIndex < 0) return;
        _app.SettingsService.Settings.FloatCount = CountCombo.SelectedIndex + 1;
        _app.SettingsService.Save();
        _app.Float?.ApplySettings();
    }

    private void OnDockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsReady || DockCombo.SelectedIndex < 0) return;
        _app.SettingsService.Settings.FloatDock = DockValues[DockCombo.SelectedIndex];
        _app.SettingsService.Save();
        _app.Float?.ApplySettings();
    }

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;

        VisibilityMode mode;
        if (ModeFullRadio.IsChecked == true) mode = VisibilityMode.Full;
        else if (ModeMiniRadio.IsChecked == true) mode = VisibilityMode.Mini;
        else mode = VisibilityMode.Hidden;

        // 硬规则即时校验
        if (mode == VisibilityMode.Hidden && UrgencyHelper.HasUrgentPending(_app.Store.Tasks))
        {
            MessageBox.Show(this,
                "存在 24 小时内到期（或已逾期）的未完成任务，不允许完全隐藏，只能降级为极简模式。",
                "无法完全隐藏", MessageBoxButton.OK, MessageBoxImage.Information);
            mode = VisibilityMode.Mini;
            ApplyModeRadios(mode);
        }

        _app.SettingsService.Settings.VisibilityMode = mode;
        _app.SettingsService.Save();
        _app.Float?.ApplySettings();
    }

    private void OnMouseThroughChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        var on = MouseThroughCheck.IsChecked == true;
        _app.SettingsService.Settings.MouseThrough = on;
        _app.SettingsService.Save();
        _app.Float?.ApplyWindowStylesPublic();
    }

    private void OnFullscreenChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        _app.SettingsService.Settings.FullscreenAutoHide = FullscreenCheck.IsChecked == true;
        _app.SettingsService.Save();
        _app.Float?.UpdateContent();
    }

    private void OnFloatBgOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsReady) return;
        _app.SettingsService.Settings.FloatBackgroundOpacity = Math.Clamp(e.NewValue, 0.0, 1.0);
        BgOpacityLabel.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        _app.SettingsService.Save();
        _app.Float?.ApplySettings();
    }

    private void OnFloatTextOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsReady) return;
        _app.SettingsService.Settings.FloatTextOpacity = Math.Clamp(e.NewValue, 0.3, 1.0);
        TextOpacityLabel.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        _app.SettingsService.Save();
        _app.Float?.ApplySettings();
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        var theme = ThemeDarkRadio.IsChecked == true ? AppTheme.Dark : AppTheme.Light;
        if (_app.SettingsService.Settings.ThemeMode == theme)
            return;
        _app.SettingsService.Settings.ThemeMode = theme;
        _app.SettingsService.Save();
        ThemeService.Apply(theme); // DynamicResource 自动刷新；浮窗等由 Changed 事件重建
        SetStatus(theme == AppTheme.Dark ? "已切换为暗色模式" : "已切换为浅色模式");
    }

    private void OnBackdropChecked(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        var mode = BackdropAcrylicRadio.IsChecked == true ? WindowBackground.Acrylic
            : BackdropSolidRadio.IsChecked == true ? WindowBackground.Solid
            : WindowBackground.Mica;
        if (_app.SettingsService.Settings.Background == mode)
            return;
        _app.SettingsService.Settings.Background = mode;
        _app.SettingsService.Save();
        ThemeService.ApplyBackground(mode);
        SetStatus(mode switch
        {
            WindowBackground.Acrylic => "窗口背景：亚克力",
            WindowBackground.Solid => "窗口背景：纯色",
            _ => "窗口背景：云母",
        });
    }

    private void OnReminderDefaultChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        var plan = _app.SettingsService.Settings.DefaultReminderPlan;
        plan.Enable7d = Rem7dCheck.IsChecked == true;
        plan.Enable3dDaily = RemDailyCheck.IsChecked == true;
        plan.Enable24hFrequent = Rem24hCheck.IsChecked == true;

        if (TimeSpan.TryParseExact(DailyTimeBox.Text, new[] { "h\\:mm", "hh\\:mm" },
                System.Globalization.CultureInfo.InvariantCulture, out var t))
        {
            plan.DailyTime = DailyTimeBox.Text.Trim();
        }

        var idx = FreqHoursCombo.SelectedIndex;
        if (idx >= 0)
            plan.FreqHours = new[] { 2, 3, 4, 6 }[idx];

        _app.SettingsService.Save();
    }

    private void OnHotkeyEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        var enabled = HotkeyEnabledCheck.IsChecked == true;
        _app.SettingsService.Settings.HotkeyEnabled = enabled;
        _app.SettingsService.Save();

        if (enabled)
        {
            if (!_app.Hotkey.Register(_app.SettingsService.Settings))
            {
                MessageBox.Show(this,
                    "热键 Ctrl+Alt+D 已被其他程序占用。\n点击「当前按键」输入框，按下新的组合键即可改键。",
                    "热键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
                _app.SettingsService.Settings.HotkeyEnabled = false;
                _settingsReady = false;
                HotkeyEnabledCheck.IsChecked = false;
                _settingsReady = true;
                _app.SettingsService.Save();
            }
        }
        else
        {
            _app.Hotkey.Unregister();
        }
    }

    private void OnHotkeyBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        HotkeyBox.Focus();
        Keyboard.Focus(HotkeyBox);
        HotkeyBox.Text = "请按新的组合键…";
        e.Handled = true;
    }

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return; // 等待真正的主键
        }

        var mods = ToModifierBits(Keyboard.Modifiers);
        if (mods == 0)
        {
            HotkeyBox.Text = "需要包含 Ctrl / Alt / Shift / Win 修饰键";
            e.Handled = true;
            return;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var settings = _app.SettingsService.Settings;
        settings.HotkeyModifiers = mods;
        settings.HotkeyKey = vk;

        if (!_app.Hotkey.Register(settings))
        {
            MessageBox.Show(this,
                $"组合键 {FormatHotkey(mods, vk)} 无法注册（可能被占用或系统预留）。",
                "热键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            HotkeyBox.Text = FormatHotkey(settings.HotkeyModifiers, settings.HotkeyKey);
        }
        else
        {
            HotkeyBox.Text = FormatHotkey(mods, vk);
            SetStatus($"全局热键已更新为 {FormatHotkey(mods, vk)}");
        }
        _app.SettingsService.Save();
        e.Handled = true;
    }

    private void OnHotkeyBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_settingsReady) return;
        var s = _app.SettingsService.Settings;
        HotkeyBox.Text = FormatHotkey(s.HotkeyModifiers, s.HotkeyKey);
    }

    private static uint ToModifierBits(ModifierKeys keys)
    {
        uint bits = 0;
        if (keys.HasFlag(ModifierKeys.Control)) bits |= NativeHotkey.ModControl;
        if (keys.HasFlag(ModifierKeys.Alt)) bits |= NativeHotkey.ModAlt;
        if (keys.HasFlag(ModifierKeys.Shift)) bits |= NativeHotkey.ModShift;
        if (keys.HasFlag(ModifierKeys.Windows)) bits |= NativeHotkey.ModWin;
        return bits;
    }

    public static string FormatHotkey(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & NativeHotkey.ModControl) != 0) parts.Add("Ctrl");
        if ((mods & NativeHotkey.ModAlt) != 0) parts.Add("Alt");
        if ((mods & NativeHotkey.ModShift) != 0) parts.Add("Shift");
        if ((mods & NativeHotkey.ModWin) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        parts.Add(key.ToString().Length == 1 ? key.ToString().ToUpperInvariant() : key.ToString());
        return string.Join("+", parts);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        var enabled = AutoStartCheck.IsChecked == true;
        var ok = AutoStartService.SetEnabled(enabled);
        if (ok)
        {
            _app.SettingsService.Settings.AutoStart = enabled;
            _app.SettingsService.Save();
            SetStatus(enabled ? "已开启开机自启动" : "已关闭开机自启动");
        }
        else
        {
            MessageBox.Show(this, "写入注册表失败，未能修改开机自启动。", "开机自启",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _settingsReady = false;
            AutoStartCheck.IsChecked = _app.SettingsService.Settings.AutoStart;
            _settingsReady = true;
        }
    }

    private void OnMorningChanged(object sender, RoutedEventArgs e)
    {
        if (!_settingsReady) return;
        _app.SettingsService.Settings.MorningReportEnabled = MorningCheck.IsChecked == true;
        _app.SettingsService.Save();
    }

    #endregion
}

/// <summary>任务行展示模型（显示文本均为刷新时计算）。</summary>
public class TaskRowVm
{
    public DdlTask Task { get; }
    public TaskRowVm(DdlTask task) => Task = task;

    public string Title => Task.Title;
    public string CheckMark => Task.IsCompleted ? "✓" : "○";
    public string CheckToolTip
    {
        get
        {
            if (Task.IsCompleted) return "已完成，点击可恢复";
            if (Task.HasProgress && Task.DoneUnits < Task.TotalUnits!.Value)
                return $"点击完成 1 {Task.UnitText}（进度 {Task.DoneUnits}/{Task.TotalUnits}）";
            return "点击标记完成";
        }
    }
    public Brush CheckColor => Task.IsCompleted
        ? ThemeService.Get("UrgencySafeBrush")
        : ThemeService.Get("TextMutedBrush");

    public FontWeight TitleWeight
        => UrgencyHelper.Level(Task) == UrgencyLevel.Overdue ? FontWeights.Bold : FontWeights.Normal;

    public Brush UrgencyBrush => UrgencyHelper.LevelBrush(UrgencyHelper.Level(Task));

    public string UrgencyName => UrgencyHelper.LevelName(UrgencyHelper.Level(Task));

    public Brush BadgeBrush
    {
        get
        {
            var level = UrgencyHelper.Level(Task);
            return UrgencyHelper.LevelBrush(level);
        }
    }

    public string MetaLine
    {
        get
        {
            var parts = new List<string>
            {
                ImportExportService.CategoryName(Task.Category),
                $"截止 {Task.Deadline:yyyy-MM-dd HH:mm}",
                UrgencyHelper.RelativeText(Task),
            };

            if (Task.HasProgress)
            {
                parts.Add($"进度 {Task.DoneUnits}/{Task.TotalUnits} {Task.UnitName}".Trim());
            }
            if (Task.SubTasks.Count > 0)
            {
                var done = Task.SubTasks.Count(s => s.IsDone);
                parts.Add($"子任务 {done}/{Task.SubTasks.Count}");
            }
            if (Task.Recurrence != Recurrence.None)
            {
                parts.Add(Task.Recurrence switch
                {
                    Recurrence.Weekly => "每周重复",
                    Recurrence.Biweekly => "每两周重复",
                    _ => "每月重复",
                });
            }
            if (!string.IsNullOrWhiteSpace(Task.Note))
                parts.Add($"备注：{Task.Note}");
            if (Task.IsCompleted && Task.CompletedAt is DateTime c)
                parts.Add($"完成于 {c:MM-dd HH:mm}");

            return string.Join("　·　", parts);
        }
    }
}

/// <summary>最近完成记录行。</summary>
public class RecentItemVm
{
    public string WhenText { get; init; } = "";
    public string Title { get; init; } = "";
}

/// <summary>绿色对勾完成动效（缩放 + 淡出）。</summary>
public class CheckmarkAdorner : Adorner
{
    private readonly FormattedText _formatted;
    private readonly Point _center;

    public CheckmarkAdorner(UIElement adorned, Point center) : base(adorned)
    {
        _center = center;
        var typeface = new Typeface(new FontFamily("Segoe UI Symbol"), FontStyles.Normal,
            FontWeights.Bold, FontStretches.Normal);
        _formatted = new FormattedText("✓",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface, 64,
            new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
            96);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(0.2, 0.2);
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawText(_formatted, new Point(_center.X - _formatted.Width / 2, _center.Y - _formatted.Height / 2));
    }

    public void RunThenRemove(AdornerLayer layer)
    {
        var scale = new DoubleAnimation(0.2, 1.25, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = TimeSpan.FromMilliseconds(280),
        };

        var transform = (ScaleTransform)RenderTransform;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        BeginAnimation(OpacityProperty, fade);

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(760),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            layer.Remove(this);
        };
        timer.Start();
    }
}

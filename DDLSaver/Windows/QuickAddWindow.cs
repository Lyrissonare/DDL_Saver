using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DDLSaver.Models;
using DDLSaver.Services;

namespace DDLSaver.Windows;

/// <summary>
/// 快速添加小窗（规格 3.2）：仅 标题 + 截止 + 分类 三个字段，10 秒内录完。
/// 全局热键 Ctrl+Alt+D 呼出；回车直接保存；非模态，可重复弹出复用。
/// </summary>
public class QuickAddWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly App _app;
    private readonly TextBox _titleBox = new()
    {
        FontSize = 15,
    };
    private readonly ComboBox _categoryCombo = new() { FontSize = 14 };
    private readonly DatePicker _datePicker = new() { FontSize = 14 };
    private readonly TextBox _timeBox = new()
    {
        FontSize = 14,
        Width = 76,
        Text = "23:59",
    };
    private readonly TextBlock _hint = new()
    {
        FontSize = 11.5,
    };

    private static readonly string[] Categories = { "网课", "小组作业", "考试", "课程作业", "杂事" };
    private static readonly TaskCategory[] CategoryValues =
        { TaskCategory.OnlineCourse, TaskCategory.GroupProject, TaskCategory.Exam, TaskCategory.Homework, TaskCategory.Misc };

    public QuickAddWindow(App app)
    {
        _app = app;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowBackground(this);
        Title = "快速添加";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica;
        WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference.Round;
        ExtendsContentIntoTitleBar = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        foreach (var name in Categories)
            _categoryCombo.Items.Add(name);
        _categoryCombo.SelectedIndex = 0;

        _datePicker.SelectedDate = DateTime.Today;

        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = new Wpf.Ui.Controls.TitleBar
        {
            Title = "快速添加",
            Icon = new Wpf.Ui.Controls.ImageIcon { Source = app.CreateAppIcon() },
            Height = 46,
            Padding = new Thickness(14, 0, 0, 0),
            FontSize = 12.5,
            ShowMinimize = false,
            ShowMaximize = false,
        };
        root.Children.Add(titleBar);

        var main = new Grid { Margin = new Thickness(16, 6, 16, 16) };
        Grid.SetRow(main, 1);
        root.Children.Add(main);
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 5; i++)
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddMain(UIElement el, int row, int col)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            main.Children.Add(el);
        }

        void AddTo(Grid g, UIElement el, int col, int row)
        {
            Grid.SetRow(el, row);
            Grid.SetColumn(el, col);
            g.Children.Add(el);
        }

        AddMain(Label("标题 *"), 0, 0);

        var grid2 = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        grid2.ColumnDefinitions.Add(new ColumnDefinition());
        grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddTo(grid2, _titleBox, 0, 0);
        var clear = new Button { Content = "✕", Width = 32, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => _titleBox.Clear();
        AddTo(grid2, clear, 1, 0);
        AddMain(grid2, 0, 1);

        // 截止行
        var deadlineRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        deadlineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        deadlineRow.ColumnDefinitions.Add(new ColumnDefinition());
        deadlineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        deadlineRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddTo(deadlineRow, Label("截止"), 0, 0);
        AddTo(deadlineRow, _datePicker, 1, 0);
        AddTo(deadlineRow, _timeBox, 2, 0);
        var todayBtn = new Button { Content = "今天", Margin = new Thickness(6, 0, 0, 0) };
        todayBtn.Click += (_, _) => _datePicker.SelectedDate = DateTime.Today;
        AddTo(deadlineRow, todayBtn, 3, 0);
        AddMain(deadlineRow, 1, 0);

        // 分类行
        Grid.SetColumnSpan(deadlineRow, 2);
        var catRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        catRow.Children.Add(Label("分类"));
        _categoryCombo.Width = 130;
        catRow.Children.Add(_categoryCombo);
        AddMain(catRow, 2, 0);
        Grid.SetColumnSpan(catRow, 2);

        // 底部：提示 + 保存
        var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hint.Text = $"按 Enter 保存 · 热键 {MainWindow.FormatHotkey(app.SettingsService.Settings.HotkeyModifiers, app.SettingsService.Settings.HotkeyKey)} 随时呼出";
        AddTo(bottom, _hint, 0, 0);
        var save = new Wpf.Ui.Controls.Button
        {
            Content = "保存",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            IsDefault = true,
            MinWidth = 88,
            Margin = new Thickness(12, 0, 0, 0),
        };
        save.Click += (_, _) => Save();
        AddTo(bottom, save, 1, 0);
        AddMain(bottom, 3, 0);
        Grid.SetColumnSpan(bottom, 2);

        Content = root;

        // Enter 保存（任意输入框）
        _titleBox.KeyDown += OnKeyDown;
        _timeBox.KeyDown += OnKeyDown;
        _datePicker.KeyDown += OnKeyDown;
        _categoryCombo.KeyDown += OnKeyDown;

        Loaded += (_, _) =>
        {
            _titleBox.Focus();
            _titleBox.SelectAll();
        };
    }

    private static TextBlock Label(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Margin = new Thickness(0, 0, 10, 0),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return block;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Save();
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Save()
    {
        var title = _titleBox.Text.Trim();
        if (title.Length == 0)
        {
            _hint.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            _hint.Text = "标题不能为空！";
            _titleBox.Focus();
            return;
        }

        if (_datePicker.SelectedDate is not DateTime date)
        {
            _hint.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            _hint.Text = "请选择截止日期！";
            return;
        }

        var timeText = string.IsNullOrWhiteSpace(_timeBox.Text) ? "23:59" : _timeBox.Text.Trim();
        if (!TimeSpan.TryParseExact(timeText, new[] { "h\\:mm", "hh\\:mm" },
                System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            _hint.Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
            _hint.Text = "时间格式应为 HH:mm（如 23:59）";
            return;
        }

        var task = new DdlTask
        {
            Title = title,
            Category = CategoryValues[Math.Max(0, _categoryCombo.SelectedIndex)],
            Deadline = date.Date.Add(time),
            Reminders = _app.SettingsService.Settings.DefaultReminderPlan.Clone(),
            CreatedAt = DateTime.Now,
        };
        _app.Store.Tasks.Add(task);
        _app.Store.NotifyChanged();
        _app.SetStatus($"已快速添加「{task.Title}」，截止 {task.Deadline:MM月dd日 HH:mm}");

        // 快速连续录入：清空标题但保留字段
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _hint.Text = $"按 Enter 保存 · 热键 {MainWindow.FormatHotkey(_app.SettingsService.Settings.HotkeyModifiers, _app.SettingsService.Settings.HotkeyKey)} 随时呼出";
        _titleBox.Clear();
        _titleBox.Focus();
    }

    /// <summary>呼出（已有实例则聚焦）。</summary>
    public void ActivateQuickAdd()
    {
        // Topmost 交由每次激活设置，防止被其他窗口压住
        Topmost = true;
        if (!IsVisible)
            Show();
        Activate();

        // 强制定位到前台（后台进程触发热键时 Activate 可能被系统剥夺）
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                Interop.NativeMethods.ShowWindow(hwnd, 5); // SW_SHOW
                Interop.NativeMethods.SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // 忽略：前台抢占失败时窗口仍可见
        }

        _titleBox.Focus();
        Keyboard.Focus(_titleBox);
        _titleBox.SelectAll();
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DDLSaver.Core;
using DDLSaver.Models;
using DDLSaver.Services;

namespace DDLSaver.Windows;

/// <summary>
/// 今日早报（规格 3.4）：启动时若跨天（上次启动日期 ≠ 今天），汇总
/// 今日到期 / 明日到期 / 已逾期 / 各进度任务今日应完成量。
/// 可勾选"不再显示"。
/// </summary>
public class MorningReportWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly App _app;
    private readonly CheckBox _nothingCheck;

    public MorningReportWindow(App app)
    {
        _app = app;
        SourceInitialized += (_, _) => ThemeService.ApplyWindowBackground(this);
        Title = "今日早报";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica;
        WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference.Round;
        ExtendsContentIntoTitleBar = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new Wpf.Ui.Controls.TitleBar
        {
            Title = "今日早报",
            Icon = new Wpf.Ui.Controls.ImageIcon { Source = app.CreateAppIcon() },
            Height = 46,
            Padding = new Thickness(14, 0, 0, 0),
            FontSize = 12.5,
            ShowMinimize = false,
            ShowMaximize = false,
        });

        var content = new StackPanel { Margin = new Thickness(20, 6, 20, 18) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        var heading = new TextBlock
        {
            Text = $"📋 今日早报 · {DateTime.Today:M月d日 dddd}",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        content.Children.Add(heading);

        var sections = new StackPanel();
        BuildSections(sections);
        content.Children.Add(sections);

        _nothingCheck = new CheckBox
        {
            Content = "不再显示早报（可在「设置 → 系统」中重新开启）",
            Margin = new Thickness(0, 14, 0, 0),
        };
        content.Children.Add(_nothingCheck);

        var btn = new Wpf.Ui.Controls.Button
        {
            Content = "知道了",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            MinWidth = 96,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
        };
        btn.Click += (_, _) =>
        {
            _app.SettingsService.Settings.MorningReportEnabled = _nothingCheck.IsChecked != true;
            _app.SettingsService.Save();
            DialogResult = true;
        };
        content.Children.Add(btn);

        Content = root;
    }

    private void BuildSections(StackPanel content)
    {
        var now = DateTime.Now;
        var tasks = _app.Store.Tasks.Where(t => !t.IsCompleted).ToList();
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);

        void Section(string title, List<DdlTask> items, string colorKey, Func<DdlTask, string> format)
        {
            var head = new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 4),
            };
            head.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            content.Children.Add(head);

            if (items.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "　（无）",
                    FontSize = 12.5,
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                content.Children.Add(empty);
                return;
            }

            foreach (var t in items)
            {
                var line = new TextBlock
                {
                    Text = $"　·　{format(t)}",
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                };
                line.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                content.Children.Add(line);
            }
        }

        var overdue = tasks.Where(t => UrgencyHelper.IsOverdue(t, now))
            .OrderBy(t => t, new UrgencyComparer())
            .ToList();
        Section("🔴 已逾期（今日必须优先处理）", overdue,
            "UrgencyOverdueBrush",
            t => $"{t.Title} 已逾期 {UrgencyHelper.OverdueDays(t)} 天，截止 {t.Deadline:MM-dd HH:mm}");

        var dueToday = tasks
            .Where(t => !UrgencyHelper.IsOverdue(t, now) && t.Deadline.Date == today)
            .OrderBy(t => t.Deadline)
            .ToList();
        Section("🗓 今日到期", dueToday,
            "UrgencyCriticalBrush",
            t => $"{t.Title} · 截止 {t.Deadline:HH:mm}" + UrgencyHelper.ProgressSuffix(t));

        var dueTomorrow = tasks
            .Where(t => t.Deadline.Date == tomorrow)
            .OrderBy(t => t.Deadline)
            .ToList();
        Section("⏰ 明日到期", dueTomorrow,
            "UrgencyMediumBrush",
            t => $"{t.Title} · 截止 {t.Deadline:HH:mm}" + UrgencyHelper.ProgressSuffix(t));

        var progress = tasks
            .Where(t => t.HasProgress && t.RemainingUnits > 0)
            .OrderBy(t => t, new UrgencyComparer())
            .ToList();
        Section("📈 进度任务 · 今日应完成", progress,
            "UrgencySafeBrush",
            t =>
            {
                var suffix = UrgencyHelper.ProgressSuffix(t);
                var perDay = suffix.Length > 0 ? suffix.Split("每天").Last().Trim() : "—";
                return $"{t.Title}：还需 {t.RemainingUnits}{t.UnitName}，今天应完成 {perDay}";
            });
    }
}

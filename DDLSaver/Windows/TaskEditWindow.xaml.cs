using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DDLSaver.Core;
using DDLSaver.Models;
using DDLSaver.Services;

namespace DDLSaver.Windows;

/// <summary>
/// 任务新建 / 编辑对话框。
/// 编辑时先在工作副本上修改，点「保存」才写回数据（取消不产生半成品）。
/// </summary>
public partial class TaskEditWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DataStore _store;
    private readonly DdlTask? _original;   // null = 新建
    private readonly DdlTask _working;     // 工作副本

    private static readonly string[] CategoryNames = { "网课", "小组作业", "考试", "课程作业", "杂事" };
    private static readonly TaskCategory[] CategoryValues =
        { TaskCategory.OnlineCourse, TaskCategory.GroupProject, TaskCategory.Exam, TaskCategory.Homework, TaskCategory.Misc };

    private static readonly string[] RecurrenceNames = { "不重复", "每周", "每两周", "每月" };
    private static readonly Recurrence[] RecurrenceValues =
        { Recurrence.None, Recurrence.Weekly, Recurrence.Biweekly, Recurrence.Monthly };

    public TaskEditWindow(DataStore store, DdlTask? original, ReminderPlan defaults)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyWindowBackground(this);
        _store = store;
        _original = original;

        // 工作副本：新建则套用默认提醒方案 + 今天 23:59
        if (original is null)
        {
            _working = new DdlTask
            {
                Deadline = DateTime.Today.AddHours(23).AddMinutes(59),
                Reminders = defaults?.Clone() ?? new ReminderPlan(),
                CreatedAt = DateTime.Now,
            };
            Title = "新建任务";
        }
        else
        {
            _working = original.CloneBase();
            Title = "编辑任务";
        }

        foreach (var name in CategoryNames)
            CategoryCombo.Items.Add(name);
        CategoryCombo.SelectedIndex = Math.Max(0, Array.IndexOf(CategoryValues, _working.Category));

        foreach (var name in RecurrenceNames)
            RecurrenceCombo.Items.Add(name);
        RecurrenceCombo.SelectedIndex = Math.Max(0, Array.IndexOf(RecurrenceValues, _working.Recurrence));

        foreach (var h in new[] { 2, 3, 4, 6 })
            FreqHoursCombo.Items.Add($"{h} 小时");
        FreqHoursCombo.SelectedIndex = Math.Max(0, new[] { 2, 3, 4, 6 }.ToList().IndexOf(_working.Reminders.FreqHours));

        TitleBox.Text = _working.Title;
        DeadlineDatePicker.SelectedDate = _working.Deadline.Date;
        DeadlineTimeBox.Text = _working.Deadline.ToString("HH:mm");
        TotalBox.Text = _working.TotalUnits?.ToString() ?? "";
        DoneBox.Text = _working.DoneUnits.ToString();
        UnitBox.Text = _working.UnitName ?? "";
        NoteBox.Text = _working.Note ?? "";
        Rem7dCheck.IsChecked = _working.Reminders.Enable7d;
        RemDailyCheck.IsChecked = _working.Reminders.Enable3dDaily;
        Rem24hCheck.IsChecked = _working.Reminders.Enable24hFrequent;
        DailyTimeBox.Text = _working.Reminders.DailyTime;

        SubTaskList.ItemsSource = _working.SubTasks;

        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void OnQuickTime(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string t })
            DeadlineTimeBox.Text = t;
    }

    private void OnAddSubTask(object sender, RoutedEventArgs e)
    {
        var st = new SubTask
        {
            Title = "新子任务",
            IsDone = false,
            Note = "",
        };
        _working.SubTasks.Add(st);
        // 触发 ItemsControl 刷新（列表本身就是引用，直接赋值同样生效）
        SubTaskList.ItemsSource = null;
        SubTaskList.ItemsSource = _working.SubTasks;
    }

    private void OnRemoveSubTask(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SubTask st })
        {
            _working.SubTasks.Remove(st);
            SubTaskList.ItemsSource = null;
            SubTaskList.ItemsSource = _working.SubTasks;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // ---- 标题（必填） ----
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            MessageBox.Show(this, "标题不能为空。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            TitleBox.Focus();
            return;
        }

        // ---- 截止时间 ----
        if (DeadlineDatePicker.SelectedDate is not DateTime date)
        {
            MessageBox.Show(this, "请选择截止日期。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            DeadlineDatePicker.Focus();
            return;
        }

        var timeText = string.IsNullOrWhiteSpace(DeadlineTimeBox.Text) ? "23:59" : DeadlineTimeBox.Text.Trim();
        if (!TimeSpan.TryParseExact(timeText, new[] { "h\\:mm", "hh\\:mm" },
                System.Globalization.CultureInfo.InvariantCulture, out var time))
        {
            MessageBox.Show(this, "时间格式应为 HH:mm，例如 23:59。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            DeadlineTimeBox.Focus();
            return;
        }
        var deadline = date.Date.Add(time);

        // ---- 进度 ----
        var totalText = TotalBox.Text.Trim();
        int? total = null;
        if (totalText.Length > 0)
        {
            if (!int.TryParse(totalText, out var tv) || tv < 0)
            {
                MessageBox.Show(this, "进度总量应为非负整数。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                TotalBox.Focus();
                return;
            }
            total = tv;
        }

        var doneText = DoneBox.Text.Trim();
        var done = 0;
        if (doneText.Length > 0 && (!int.TryParse(doneText, out done) || done < 0))
        {
            MessageBox.Show(this, "已完成量应为非负整数。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            DoneBox.Focus();
            return;
        }
        if (total.HasValue && done > total.Value)
        {
            MessageBox.Show(this, "已完成量不能大于总量。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            DoneBox.Focus();
            return;
        }

        var unit = UnitBox.Text.Trim();
        var timeText2 = DailyTimeBox.Text.Trim();
        if (timeText2.Length > 0 && !TimeSpan.TryParseExact(timeText2, new[] { "h\\:mm", "hh\\:mm" },
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            MessageBox.Show(this, "提醒的每日时间格式应为 HH:mm，例如 09:00。", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            DailyTimeBox.Focus();
            return;
        }

        // ---- 写回工作副本 ----
        _working.Title = title;
        _working.Category = CategoryValues[Math.Max(0, CategoryCombo.SelectedIndex)];
        _working.Deadline = deadline;
        _working.TotalUnits = total;
        _working.DoneUnits = done;
        _working.UnitName = unit.Length > 0 ? unit : null;
        _working.Recurrence = RecurrenceValues[Math.Max(0, RecurrenceCombo.SelectedIndex)];
        _working.Note = NoteBox.Text.Trim();
        _working.Reminders.Enable7d = Rem7dCheck.IsChecked == true;
        _working.Reminders.Enable3dDaily = RemDailyCheck.IsChecked == true;
        _working.Reminders.Enable24hFrequent = Rem24hCheck.IsChecked == true;
        _working.Reminders.DailyTime = timeText2.Length > 0 ? timeText2 : "09:00";
        var freqIdx = FreqHoursCombo.SelectedIndex;
        if (freqIdx >= 0)
            _working.Reminders.FreqHours = new[] { 2, 3, 4, 6 }[freqIdx];

        // ---- 写回数据 ----
        if (_original is null)
        {
            _store.Tasks.Add(_working);
        }
        else
        {
            var deadlineChanged = _original.Deadline != _working.Deadline;
            _original.Title = _working.Title;
            _original.Category = _working.Category;
            _original.Deadline = _working.Deadline;
            _original.TotalUnits = _working.TotalUnits;
            _original.DoneUnits = _working.DoneUnits;
            _original.UnitName = _working.UnitName;
            _original.Recurrence = _working.Recurrence;
            _original.SubTasks = _working.SubTasks.Select(s => s.Clone()).ToList();
            _original.Note = _working.Note;
            _original.Reminders = _working.Reminders.Clone();
            if (deadlineChanged)
                _original.LastNotified.Clear(); // 截止时间变化 → 重新计算提醒周期
        }

        _store.NotifyChanged();
        DialogResult = true;
    }
}

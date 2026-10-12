using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public sealed class HistoryWindow : Form
{
    private readonly ListBox list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(HistoryRow.Label) };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    private sealed record HistoryRow(Guid Id, string Label);

    public HistoryWindow()
    {
        Text = "voice-switch 履歴（30分・最大50件）";
        Width = 620;
        Height = 360;
        var copy = new Button { Text = "選択をコピー", AutoSize = true };
        var clear = new Button { Text = "すべて消去", AutoSize = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        actions.Controls.AddRange([copy, clear]);
        Controls.Add(list);
        Controls.Add(actions);
        copy.Click += (_, _) => CopySelected();
        list.DoubleClick += (_, _) => CopySelected();
        clear.Click += (_, _) => { DictationHistory.Shared.Clear(); Reload(); };
        refresh.Tick += (_, _) => Reload();
        Shown += (_, _) => { Reload(); refresh.Start(); };
        FormClosed += (_, _) => refresh.Dispose();
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
            if (e.KeyCode == Keys.Enter) { CopySelected(); e.Handled = true; }
        };
    }

    private void Reload()
    {
        var selected = (list.SelectedItem as HistoryRow)?.Id;
        var entries = DictationHistory.Shared.Snapshot();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var entry in entries)
        {
            var text = entry.Text.Replace('\r', ' ').Replace('\n', ' ');
            list.Items.Add(new HistoryRow(entry.Id, $"{entry.CreatedAt.LocalDateTime:HH:mm:ss} [{entry.Origin.RuleName}] {text[..Math.Min(text.Length, 180)]}"));
        }
        for (var i = 0; i < list.Items.Count; i++)
            if (((HistoryRow)list.Items[i]).Id == selected) list.SelectedIndex = i;
        list.EndUpdate();
    }

    private void CopySelected()
    {
        if (list.SelectedItem is not HistoryRow row || DictationHistory.Shared.Select(row.Id) is not { } text) return;
        try { Clipboard.SetText(text); Text = "コピーしました。貼り付けは行いません"; }
        catch (System.Runtime.InteropServices.ExternalException) { Text = "クリップボードを使用できません"; }
    }
}

namespace AiCli.Cli.Commands;

/// <summary>
/// 实时任务清单渲染器：任务逐条追加，完成后打勾，视觉上类似 Claude Code 的任务列表。
/// 所有未完成条目显示 Braille 旋转动画，完成后变为绿色 ✓。
/// </summary>
internal sealed class LiveTaskListRenderer
{
    private readonly List<Entry> _entries = new();
    private int _liveLines = 0;
    private int _tick = 0;

    private static readonly string[] Frames =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    // ── 公共 API ──────────────────────────────────────────────────────────────

    /// <summary>追加一条正在运行的任务条目并实时重绘。</summary>
    public void Add(string label)
    {
        _entries.Add(new Entry(label, false));
        Redraw();
    }

    /// <summary>将最后一条未完成条目标记为完成，保留原标签。</summary>
    public void CompleteLast()
    {
        var idx = FindLastPending();
        if (idx >= 0)
            _entries[idx] = _entries[idx] with { Done = true };
        Redraw();
    }

    /// <summary>将最后一条未完成条目标记为完成并同时更新标签（用于思考耗时摘要）。</summary>
    public void CompleteLastWith(string label)
    {
        var idx = FindLastPending();
        if (idx >= 0)
            _entries[idx] = new Entry(label, true);
        Redraw();
    }

    /// <summary>更新最后一条未完成条目的标签（用于思考 token 滚动预览）。</summary>
    public void UpdateLast(string label)
    {
        var idx = FindLastPending();
        if (idx >= 0)
            _entries[idx] = _entries[idx] with { Label = label };
        Redraw();
    }

    /// <summary>
    /// 清除屏幕上的动态行，然后将所有条目以永久行打印出来（执行完毕后调用）。
    /// </summary>
    public void PrintCompleted()
    {
        EraseLines(_liveLines);
        _liveLines = 0;

        foreach (var e in _entries)
        {
            var icon = e.Done ? "\x1b[32m✓\x1b[0m" : "\x1b[33m◌\x1b[0m";
            System.Console.WriteLine($"  {icon} \x1b[2m{e.Label}\x1b[0m");
        }
    }

    // ── 内部实现 ──────────────────────────────────────────────────────────────

    private int FindLastPending() =>
        _entries.FindLastIndex(e => !e.Done);

    private void Redraw()
    {
        EraseLines(_liveLines);
        int lines = 0;

        foreach (var e in _entries)
        {
            if (e.Done)
            {
                System.Console.WriteLine($"  \x1b[32m✓\x1b[0m \x1b[2m{e.Label}\x1b[0m");
            }
            else
            {
                var frame = Frames[_tick % Frames.Length];
                System.Console.WriteLine($"  \x1b[33m{frame}\x1b[0m {e.Label}");
            }
            lines++;
        }

        _tick++;
        _liveLines = lines;
    }

    private static void EraseLines(int n)
    {
        for (int i = 0; i < n; i++)
            System.Console.Write("\x1b[A\r\x1b[2K");
    }

    private record Entry(string Label, bool Done);
}

namespace AiCli.Cli.Commands;

/// <summary>
/// 共享的思考 token 终端渲染逻辑：滚动显示最后 2 行，支持原地覆盖。
/// </summary>
internal static class AgentThinkingRenderer
{
    private const int MaxLines    = 2;
    private const int MaxLineWidth = 80;

    /// <summary>
    /// 渲染思考内容，返回当前屏幕上的行数（供下次调用传入 prevLines）。
    /// </summary>
    public static int Render(string thinking, int prevLines)
    {
        var segments = SplitIntoDisplayLines(thinking, MaxLineWidth);
        var visible  = segments.Skip(Math.Max(0, segments.Count - MaxLines)).ToList();

        Clear(prevLines);

        foreach (var line in visible)
            System.Console.WriteLine($"\x1b[2m  ◆ {line}\x1b[0m");

        return visible.Count;
    }

    /// <summary>清除 count 行思考内容。</summary>
    public static void Clear(int count)
    {
        for (int i = 0; i < count; i++)
            System.Console.Write("\x1b[A\r\x1b[2K");
    }

    private static List<string> SplitIntoDisplayLines(string text, int width)
    {
        var result = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            var remaining = rawLine.Trim();
            if (remaining.Length == 0) continue;
            while (remaining.Length > width)
            {
                result.Add(remaining[..width]);
                remaining = remaining[width..];
            }
            if (remaining.Length > 0)
                result.Add(remaining);
        }
        return result.Count > 0 ? result : new List<string> { text[..Math.Min(text.Length, width)] };
    }
}

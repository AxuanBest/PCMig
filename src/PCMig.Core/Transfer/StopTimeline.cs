using System.Diagnostics;

namespace PCMig.Core.Transfer;

/// <summary>
/// ★ P0-06(1) ★ 一次「点击停止」的分段计时事实。
///
/// 现场症状是"点停止后界面卡住/无响应"，而日志里只有 Robocopy 被强制终止这一条结果事实，
/// 缺少"卡在哪一段"的证据（ZIP 里没有 WER / Application Hang 1002 / 转储 / 录像）。
/// 因此在改任何等待逻辑之前，先把这条链路量出来：
///
///     点击 → 立即反馈（"正在停止…"可见） → 同步 Flush → 取消令牌投递
///          → 引擎收尾（Robocopy 进程退出、Receipt/JobState 落盘） → 窗口可响应
///
/// 本类型只做**记录**：单调时钟（<see cref="Stopwatch.GetTimestamp"/>），
/// 不读系统时间、不阻塞、不做任何业务判断。它绝不改变"停止是否成功"的结论——
/// 结论仍由引擎真值（IsRunning 等）决定；这里只回答"花了多少毫秒、卡在第几段"。
///
/// 线程安全：各段可能来自 UI 线程与引擎回调线程，内部用锁保护；读出来的快照是拷贝。
/// </summary>
public sealed class StopTimeline
{
    private readonly object _gate = new();
    private readonly List<StopSegment> _segments = new();
    private readonly long _startTicks;

    /// <summary>在"用户按下停止"的那一刻创建（构造即开始计时）。</summary>
    public StopTimeline(string reason = "user-stop")
    {
        Reason = reason;
        _startTicks = Stopwatch.GetTimestamp();
        // ★ 起点阶段必须是**结构上的 0**，不能在这里再读一次时钟 ★
        //   旧写法是 Mark("click") ⇒ 内部再调一次 Stopwatch.GetTimestamp()：两次调用之间若被
        //   线程池抢占，delta 会四舍五入成 1 ms，于是「click 就是 t0」这个不变量退化成了靠运气的
        //   时序断言（重负载下实测偶发失败：Assert.Equal(0, tl.MsAt("click"))）。
        lock (_gate) { _segments.Add(new StopSegment("click", 0, null)); }
    }

    /// <summary>触发原因（用户停止 / 断网 / 关闭窗口 / 测试）。</summary>
    public string Reason { get; }

    /// <summary>是否已经给出最终结果（<see cref="Complete"/> 被调用过）。</summary>
    public bool IsComplete { get; private set; }

    /// <summary>最终结果短码，例如 <c>stop-settled</c> / <c>stop-unsettled</c> / <c>stop-failed</c>。</summary>
    public string? Outcome { get; private set; }

    /// <summary>阶段记录（按发生顺序的快照拷贝）。</summary>
    public IReadOnlyList<StopSegment> Segments
    {
        get { lock (_gate) return _segments.ToArray(); }
    }

    /// <summary>从点击到现在（或到收口那一刻）的总毫秒数。</summary>
    public long TotalMs
    {
        get { lock (_gate) return ElapsedMs(); }
    }

    /// <summary>记录一个阶段。<paramref name="phase"/> 是稳定短码（会被测试与日志引用）。</summary>
    public void Mark(string phase, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        lock (_gate)
        {
            _segments.Add(new StopSegment(phase, ElapsedMs(), detail));
        }
    }

    /// <summary>收口：记一段并写下结果码。可多次调用（最后一次为准，幂等替换结果码）。</summary>
    public void Complete(string outcome, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        lock (_gate)
        {
            _segments.Add(new StopSegment("complete", ElapsedMs(), detail));
            Outcome = outcome;
            IsComplete = true;
        }
    }

    /// <summary>某阶段距点击的毫秒数；没有该阶段返回 -1。</summary>
    public long MsAt(string phase)
    {
        lock (_gate)
        {
            foreach (var s in _segments)
                if (string.Equals(s.Phase, phase, StringComparison.Ordinal))
                    return s.AtMs;
            return -1;
        }
    }

    /// <summary>是否出现过某阶段。</summary>
    public bool Has(string phase) => MsAt(phase) >= 0;

    /// <summary>两阶段之间的毫秒差；任一阶段缺失返回 -1。</summary>
    public long SpanMs(string fromPhase, string toPhase)
    {
        var a = MsAt(fromPhase);
        var b = MsAt(toPhase);
        return a < 0 || b < 0 ? -1 : b - a;
    }

    /// <summary>
    /// 一行可读摘要（写日志/诊断用）。例：
    /// <c>停止时间线[user-stop]：点击→反馈 0 ms；点击→取消投递 63 ms；点击→引擎收尾 1180 ms；总计 1184 ms；结果 stop-settled</c>
    /// </summary>
    public string ToSummary()
    {
        var parts = new List<string>();
        long Prev(string? from, string to) => from is null ? MsAt(to) : SpanMs(from, to);

        if (Has("feedback-shown")) parts.Add($"点击→反馈 {Prev(null, "feedback-shown")} ms");
        if (Has("flush-done")) parts.Add($"点击→Flush 完成 {Prev(null, "flush-done")} ms");
        if (Has("cancel-delivered")) parts.Add($"点击→取消投递 {Prev(null, "cancel-delivered")} ms");
        if (Has("engine-settled")) parts.Add($"点击→引擎收尾 {Prev(null, "engine-settled")} ms");
        if (Has("footer-pushed")) parts.Add($"点击→底栏可再操作 {Prev(null, "footer-pushed")} ms");

        var tail = IsComplete ? $"；结果 {Outcome}" : "；结果（未收口）";
        return $"停止时间线[{Reason}]：{string.Join("；", parts)}；总计 {TotalMs} ms{tail}";
    }

    private long ElapsedMs()
    {
        var ms = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
        return ms < 0 ? 0 : (long)ms;
    }
}

/// <summary>停止时间线上的一个阶段。<paramref name="AtMs"/> 是距"点击停止"的毫秒数。</summary>
public sealed record StopSegment(string Phase, long AtMs, string? Detail);
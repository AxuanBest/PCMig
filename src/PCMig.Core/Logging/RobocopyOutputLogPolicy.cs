// ★ PCMig v0.5.3 Preview.2 ★ robocopy 输出行写进任务日志的**采样策略**（P1-11）
//
// 现场铁证（私有基线 raw-logzip，JOB-20261010-152632-be58，2026-10-10）：
//   · logs\job-20261010.log   = 23,357,132 B / 117,310 行
//   · logs\job-20261010.jsonl = 42,601,763 B
//   · 其中排名前 15 的行模板**全部**是 `robocopy| 	    新文件 …` 这类**逐文件输出行**
//     （单模板最高 5,742 行），也就是说：这两个文件几乎全由 robocopy 的逐文件输出复制而来。
//
// 为什么这是缺陷：robocopy 自己已经把**逐行原文**写进了每个对象的 /UNILOG
//   （`logs\robocopy\object-XXXXXX.log`，同一任务里 18,264,496 B），
//   Serilog 再把同样的行复制一份进 job-*.log 与 job-*.jsonl ⇒ 单任务日志直逼 66 MB，
//   而且 Serilog 的 File sink 默认 `fileSizeLimitBytes = 1 GB` 且 `rollOnFileSizeLimit = false`，
//   一整天跑下来就是几十上百 MB 的**单文件**：日志目录膨胀、排障时打开/检索缓慢、
//   日志目录被日志本身填满的风险，全都由此而来。
//
// 本策略只做一件事：**给"逐行复制"设一个诚实的上限**，并让省略量可核对：
//   ① 错误行**永不省略**（含 ERROR / 错误 的所有行都会写）；
//   ② 前 HeadLines 行全记（保留"开跑现场"）；之后每 Stride 行记 1 行（保留"进度可见"）；
//   ③ 记满 MaxLoggedLines 行后停止（硬上限：单趟日志体积不再由文件数决定）；
//   ④ Observed / Logged / Dropped 三个计数**必须自洽**（Observed = Logged + Dropped），
//      调用方在省略发生时写一条 Information 摘要，并指向 /UNILOG 全文。
//
// ★ 纪律 ★ 本类型只决定"要不要写日志"，**绝不**影响 OutputLine/FileCopied 等对外事件：
//   UI 的实时行流与控制台既视感必须逐行原样保留（P1-11 不得以"日志瘦身"为名偷偷砍功能）。
namespace PCMig.Core.Logging;

/// <summary>
/// robocopy 逐行输出的日志采样器。**非线程安全**：一个实例只属于一趟 robocopy（单一泵线程）。
/// </summary>
public sealed class RobocopyOutputLogPolicy
{
    /// <summary>前多少行全记（开跑现场：命令行回显、目录行、前若干个文件）。</summary>
    public const int HeadLines = 200;

    /// <summary>头部之后每多少行记 1 行（进度可见性）。</summary>
    public const int Stride = 500;

    /// <summary>单趟最多写多少行 robocopy 输出（硬上限，错误行也计入；错误行优先于采样）。</summary>
    public const int MaxLoggedLines = 1500;

    /// <summary>被观察到的 robocopy 输出行总数（真实值）。</summary>
    public int Observed { get; private set; }

    /// <summary>真正写进日志的行数（含错误行）。</summary>
    public int Logged { get; private set; }

    /// <summary>因采样/上限被省略的行数（真实值，用于摘要里的"省略 N 行"）。</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// 这一行要不要写进日志。<paramref name="isError"/> 由调用方用**与既有错误行判据完全相同**的规则给出
    /// （见 <c>RobocopyRunner.PumpAsync</c> 里的 ERROR/错误 判断），本方法不重复实现该判据。
    /// </summary>
    public bool ShouldLog(bool isError)
    {
        Observed++;

        if (isError)
        {
            // ★ 错误行永不省略 ★ 但硬上限仍然成立：上限撞上时错误行也不再写（由摘要如实说明省略量）。
            if (Logged < MaxLoggedLines) { Logged++; return true; }
            Dropped++;
            return false;
        }

        if (Logged >= MaxLoggedLines) { Dropped++; return false; }

        // 头部全记；其后按步长采样。用 Observed（含错误行）做步长基准，
        // 这样"第 N 行"的语义与观察顺序一致，不受是否错误影响。
        var take = Observed <= HeadLines || Observed % Stride == 0;
        if (take) { Logged++; } else { Dropped++; }
        return take;
    }

    /// <summary>是否发生了省略（调用方据此决定要不要写摘要行）。</summary>
    public bool HasSkipped => Dropped > 0;

    /// <summary>
    /// 省略发生时写进任务日志的**诚实**摘要：真实总数、采样口径、被省略的行数，以及逐行全文在哪。
    /// 三个计数必须自洽，否则本方法抛 <see cref="InvalidOperationException"/>（防止后人改坏计数却不自知）。
    /// </summary>
    public string Describe(string unicodeLogPath)
    {
        if (Observed != Logged + Dropped)
        {
            throw new InvalidOperationException(
                $"robocopy 输出采样计数不自洽：Observed={Observed}，Logged={Logged}，Dropped={Dropped}。");
        }

        var where = string.IsNullOrWhiteSpace(unicodeLogPath) ? "（本趟未启用 /UNILOG）" : unicodeLogPath;
        return $"robocopy 输出共 {Observed} 行；任务日志按「前 {HeadLines} 行全记 + 之后每 {Stride} 行记 1 行」"
            + $"采样并设 {MaxLoggedLines} 行上限，已记 {Logged} 行、省略 {Dropped} 行"
            + "（错误行永不因采样被省略）。逐行原文见该对象的 robocopy 日志："
            + where + "（logs\\robocopy\\ 目录）。";
    }
}
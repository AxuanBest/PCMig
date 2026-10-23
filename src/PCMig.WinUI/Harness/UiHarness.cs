// ═══════════════════════════════════════════════════════════════════════════════
// ★ Preview.6 · 开发者 UI Harness（仅 Debug 构建）★
//
// 文件是否参与编译由 PCMig.WinUI.csproj 的
//     <ItemGroup Condition="'$(Configuration)' == 'Debug'"><Compile Include="Harness\**\*.cs" /></ItemGroup>
// 决定 —— Release / 正式发布载荷里**根本不存在这个类型**（不是"运行期关闭"，
// 是编译期不链入），满足"不得混入正式发布载荷"。
//
// 它做什么：
//   · 用真实 MainWindow 的**真实** ViewModel / 真实源码点（SourceStateDot）/
//     真实 MotionDirector / 真实主题 token 驱动指示灯；
//   · 不新增任何状态判定，不改生产判据；
//   · 不碰 SMB、不碰 robocopy、不碰迁移引擎。
//
// 两层独立测试（2026-10-22 新增）：
//   A 层（vis=a）：只驱动**视觉**——直接把真实 SourceStateDot 交给真实的
//                  MotionDirector.PlaySourceBreathing，不经过任何 ViewModel 状态机。
//                  只用来验证 Composition 动画组件本身。
//   B 层（默认）  ：走完整投影链。用 **Debug 专属的假网卡探针**（INetworkInterfaceProbe 的
//                  实现，注入 Monitor.Enter 换取 InOctets 随真实时间线性增长）让
//                  MigrationSessionViewModel.HasLiveTransferActivity **自然**为真，
//                  再让正式 16 ms 节拍把它们覆盖到 ConnectionViewModel。
//                  不压刷新、不改判据、不绕过状态机。vis=b 把探针还回真实网卡。
//
// 脚本命令：
//     sleep <ms>                等待
//     vis a|b                   切换 A 层 / B 层（默认 b）
//     phase <P>                 施加 JobPhase（Running / Paused / Completed …）
//     pause <S>                 施加引擎暂停真值（None / Pausing / Paused）
//     glob on|off               自动泵 16 ms 呈现节拍（B 层默认开）
//     pump                      手动跑一拍 RefreshFooterPresentation
//     state <T1..T10>           施加上表状态（见 Apply）
//     aplay|areset|apump <n>    A 层：起呼吸 / 复位 / 反复请求 n 次
//     burst <count> <gapMs>     高频重复调用 UpdateSourceConnectionIndicator()
//     probe <label>             打印全链路运行时事实（见 LogProbe）
//     mark <label>              界面横幅打出标记（配合录屏定位时间点；同时写入日志）
//     quit                      退出
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PCMig.Core.Native;
using PCMig.Core.Network;
using PCMig.Core.Models;
using PCMig.Core.Transfer;
using PCMig.WinUI.Presentation;

namespace PCMig.WinUI.Harness
{
    internal static class UiHarness
    {
        public const string ScriptEnvVar = "PCMIG_UI_HARNESS";

        // ── 假网卡探针（★ 仅存在于 Debug 编译单元 ★）──────────────────────────
        //
        // 为什么合法：生产代码 MigrationSessionViewModel 用的是
        //   new NetworkThroughputSampler(null, log: _log)
        // 也就是"默认探针 + 真实网卡"。探针本来就是 Core 里被设计成可注入的抽象
        // （INetworkInterfaceProbe 存在的原因就是"纯计算要与真实网卡解耦"），
        // 单测里也是这么用的。这里在 Debug Harness 里把它替换成一个"接收计数器按真实
        // 时间线性增长"的假实现，于是采样器算出来的 EMA / 稳定吞吐都是**真实计算**的，
        // 只是数据源被替换 —— 这不是绕开状态机，而是补齐它需要的真实输入条件。
        //
        // 假探针只影响"网卡吞吐读数"这一个输入，绝不伪造进度、回执、字节或落盘事实。
        private sealed class FakeCounterProbe : INetworkInterfaceProbe
        {
            internal const uint Index = 77;
            internal static ulong BytesPerSecond = 6UL * 1024 * 1024;   // 6 MB/s 恒定接收流（可由 rate 命令改）
            private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
            private ulong _carry;   // 改速率时把已累计的字节快照进来，保证 InOctets 单调

            /// <summary>
            /// 改接收速率（字节/秒）。0 表示"有网卡但无流量"——这正是 T3（已连接、未传输）
            /// 需要的真实输入条件：采样器可用、速率为 0 ⇒ HasLiveTransferActivity 为假。
            /// 只改测试数据源，不改任何生产判据。
            /// </summary>
            internal void SetRate(ulong bytesPerSecond)
            {
                lock (_clock)
                {
                    _carry = (ulong)(_clock.Elapsed.TotalSeconds * BytesPerSecond) + _carry;
                    BytesPerSecond = bytesPerSecond;
                    _clock.Restart();
                }
            }
            internal long SamplesRead;

            public InterfaceSelection SelectInterface(IReadOnlyCollection<IPAddress>? destinations, Serilog.ILogger? log = null)
            {
                Monitor.Enter(_clock);   // 换锁：让测试端能在同一把锁里重置时钟
                try
                {
                    return new InterfaceSelection(
                        InterfaceSelectionOutcome.Selected, Index,
                        "HARNESS 假网卡（Debug 专属测试数据源）", "harness-fake", 6,
                        BytesPerSecond * 8 * 10,          // 链路速率给足，避免被当成瓶颈
                        "HARNESS：Debug 专属假探针，接收计数器按真实时间线性增长",
                        "harness");
                }
                finally { Monitor.Exit(_clock); }
            }

            public bool TryReadRow(uint interfaceIndex, out InterfaceRowInfo row, out string reason)
            {
                lock (_clock)   // 注意：与 SelectInterface 用的是不同对象，见下方说明
                {
                    SamplesRead++;
                    var seconds = _clock.Elapsed.TotalSeconds;
                    row = new InterfaceRowInfo(
                        Index, "HARNESS 假网卡", "harness-fake",
                        OperStatus: 1, MediaConnectState: 1, Type: 6,
                        ReceiveLinkSpeed: BytesPerSecond * 8 * 10,
                        InOctets: (ulong)(seconds * BytesPerSecond) + _carry,   // _carry 让改速率后计数不回退
                        OutOctets: 0);
                    reason = string.Empty;
                    return true;
                }
            }
        }

        /// <summary>是否为本轮开启驱动（未设脚本 ⇒ false，完全不介入正常启动）。</summary>
        public static bool IsEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ScriptEnvVar));

        // 当前层：'a' = 视觉层，'b' = 完整投影层
        private static char _vis = 'b';

        private static MainWindow? _win;
        // 注意：不要在这里持有 Session 引用 —— 「重建会话」会换掉实例，
        // 每次用时重新取（_win.SessionForHarness）。

        public static void Start(MainWindow window)
        {
            var path = Environment.GetEnvironmentVariable(ScriptEnvVar);
            if (string.IsNullOrWhiteSpace(path)) return;
            // 不 await：让 OnLaunched 正常返回，窗口照常活起来。
            _ = RunAsync(window, path!);
        }

        private static async Task RunAsync(MainWindow window, string scriptPath)
        {
            _win = window;
            string[] lines;
            try { lines = File.ReadAllLines(scriptPath, Encoding.UTF8); }
            catch (Exception ex) { Log("读取脚本失败: " + ex.Message); return; }

            await Task.Delay(1800);           // 等首帧、主题资源、呈现节拍全部就位
            Log("=== harness start ===");
            window.HarnessBringToFront();     // 录屏必须拍到真实窗口（失败由脚本照实记录）
            Banner(window, "READY");

            foreach (var raw in lines)
            {
                var line = (raw ?? string.Empty).Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                try
                {
                    switch (parts[0].ToLowerInvariant())
                    {
                        case "sleep":
                            await Task.Delay(ParseInt(parts, 1, 1000));
                            break;

                        case "vis":
                            SetVisibility(parts.Length > 1 ? parts[1].ToLowerInvariant() : "b");
                            break;

                        case "phase":
                            ApplyPhase(parts.Length > 1 ? parts[1] : "Running");
                            break;

                        case "pause":
                            ApplyPause(parts.Length > 1 ? parts[1] : "None");
                            break;

                        case "glob":
                            ToggleGlobalPump(parts.Length > 1 && parts[1].Equals("on", StringComparison.OrdinalIgnoreCase));
                            break;

                        case "pump":
                            window.HarnessPumpFooterPresentation();
                            break;

                        case "state":
                            await ApplyAsync(window, parts.Length > 1 ? parts[1].ToUpperInvariant() : "?");
                            break;

                        case "aplay":
                            MotionDirector.PlaySourceBreathing(window.SourceStateDotForHarness);
                            Log("A play -> " + Describe(window));
                            break;

                        case "areset":
                            MotionDirector.ResetSourceBreathing(window.SourceStateDotForHarness);
                            Log("A reset -> " + Describe(window));
                            break;

                        case "apump":
                            await APumpAsync(window, ParseInt(parts, 1, 100));
                            break;

                        case "burst":
                            await BurstAsync(window, ParseInt(parts, 1, 200), ParseInt(parts, 2, 0));
                            break;

                        case "pulse":
                            Invoke(window);
                            break;

                        case "probe":
                            // ★ 只读、不改任何状态：把指示灯当前**真实**的运行时事实写进日志。
                            LogProbe(window, string.Join(' ', parts, 1, parts.Length - 1));
                            break;

                        case "rate":
                            // ★ 只改测试数据源 ★ 设置假网卡接收速率（字节/秒；0 = 有网卡无流量）。
                            // 用于构造"已连接但未传输"（采样器可用、速率 0）与"正在传输"两种输入。
                            {
                                var rv = parts.Length > 1 && ulong.TryParse(parts[1], out var rr) ? rr : 0UL;
                                _fakeProbe.SetRate(rv);
                                Log("rate -> " + rv + " B/s (" + (rv / 1024.0 / 1024.0).ToString("F2") + " MB/s)");
                            }
                            break;

                        case "shares":
                            // ★ 2026-10-23（Preview.7 收口）★ 填写"已勾选的共享根"。
                            // 存在的理由：空闲探测的目标集合来自 Shares（SelectedShareRoots），
                            // 没有勾选共享时会退回主机级枚举 —— 那测不到"指定共享持续可读"这条
                            // 核心场景。这里只把**目标清单**补上，探测、分级、落库规则全由正式代码跑。
                            ApplyShares(window, string.Join(' ', parts, 1, parts.Length - 1));
                            break;

                        case "hostprobe":
                            // ★ 2026-10-23（Preview.7 收口）★ 让**主机级共享枚举**按现场方式失败。
                            //
                            // 现场真相：对方不导出 IPC$ ⇒ NetShareEnum 恒返回 67（找不到网络名），
                            // 而指定数据共享 `\\192.0.2.10\e` 明明可读。这条命令就是复现这个不对称：
                            //   hostprobe fail  ⇒ ShareCountProbe 抛 67
                            //   hostprobe ok    ⇒ 还原真实枚举
                            // 它只替换"外部世界的回答"，探测目标怎么选、失败怎么分级、证据怎么落库
                            // 全部仍由正式代码计算。
                            ApplyHostProbe(parts.Length > 1 ? parts[1] : "fail");
                            break;

                        case "prog":
                            // ★ R2-B（2026-10-24）★ 把底栏进度条钉在指定百分比，用于真实合成帧取证。
                            // 只写投影属性；业务真值仍由会话层单向供给（先 glob off 才不会被打回）。
                            {
                                var pct = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var pp) ? pp : 0d;
                                var st = parts.Length > 2 ? parts[2] : "Running";
                                window.HarnessSetFooterProgress(pct, st);
                                Log("prog -> " + window.HarnessFooterProgressReport());
                            }
                            break;

                        case "proghold":
                            // ★ R2-B ★ 持续钉住底栏进度条（底栏节拍会把 Value 写回会话真值）。
                            {
                                var pct2 = parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var q2) ? q2 : 0d;
                                var ms2 = ParseInt(parts, 2, 4000);
                                var st2 = parts.Length > 3 ? parts[3] : "Running";
                                window.HarnessHoldFooterProgress(pct2, ms2, st2);
                                Log("proghold -> " + pct2.ToString(System.Globalization.CultureInfo.InvariantCulture) + " for " + ms2 + "ms");
                            }
                            break;

                        case "progreport":
                            Log("prog " + window.HarnessFooterProgressReport());
                            break;

                        case "mark":
                            Banner(window, string.Join(' ', parts, 1, parts.Length - 1));
                            Log("mark " + string.Join(' ', parts, 1, parts.Length - 1));
                            break;

                        case "quit":
                            Log("=== harness quit ===");
                            await Task.Delay(400);
                            Application.Current.Exit();
                            return;

                        default:
                            Log("未知命令: " + line);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log("命令失败 [" + line + "]: " + ex.Message);
                }
            }

            Log("=== harness end ===");
            await Task.Delay(800);
            Application.Current.Exit();
        }

        // ── 层切换 ────────────────────────────────────────────────────────────

        private static void SetVisibility(string v)
        {
            _vis = v.StartsWith("a", StringComparison.Ordinal) ? 'a' : 'b';
            if (_vis == 'b') InstallFakeProbe();
            else RestoreRealProbe();
            Log($"=== vis={_vis} ({(_vis == 'a' ? "视觉层：只驱动真实 SourceStateDot + MotionDirector" : "完整投影层：假网卡探针 + 正式状态机")}) ===");
        }

        private static void InstallFakeProbe()
        {
            if (_win is null) return;
            var session = _win.SessionForHarness;
            if (session is null) { Log("vis b: Session 为空，无法注入探针"); return; }

            // ★ 顺序很关键（2026-10-22 实测踩到过）：
            //   采样器只在 Host 非空时才会被创建；探针只能在已存在的采样器上替换。
            //   所以必须先把 Host 立起来，再注入探针，最后再确认一次。
            try
            {
                if (string.IsNullOrWhiteSpace(_win.ViewModel.Host)) _win.ViewModel.Host = HarnessHost;
                var diag = session.HarnessInstallThroughputProbe(_fakeProbe);
                Log("vis b: 注入诊断 " + diag);
            }
            catch (Exception ex) { Log("vis b: 注入失败 " + ex.GetType().Name + ": " + ex.Message); }
        }

        /// <summary>假探针实例（保持单例，避免多次注入时被替换成不同实例而丢时钟连续）。</summary>
        private static readonly FakeCounterProbe _fakeProbe = new();

        private static void RestoreRealProbe()
        {
            try
            {
                var session = _win?.SessionForHarness;
                session?.HarnessRestoreThroughputProbe();
                Log("vis a: 已还原真实网卡探针");
            }
            catch (Exception ex) { Log("vis a: 还原失败 " + ex.Message); }
        }

        /// <summary>主机级共享枚举的"现场复现"开关（见 hostprobe 命令）。</summary>
        private static Func<string, int>? _savedShareCountProbe;

        private static void ApplyHostProbe(string mode)
        {
            if (mode.Equals("fail", StringComparison.OrdinalIgnoreCase))
            {
                _savedShareCountProbe ??= ConnectionViewModel.ShareCountProbe;
                ConnectionViewModel.ShareCountProbe = _ =>
                    throw new IOException("枚举共享失败（错误 67）：找不到网络名。");
                Log("hostprobe -> fail（主机级枚举恒抛 67，模拟对端不导出 IPC$）");
            }
            else
            {
                if (_savedShareCountProbe is not null)
                {
                    ConnectionViewModel.ShareCountProbe = _savedShareCountProbe;
                    _savedShareCountProbe = null;
                }
                Log("hostprobe -> ok（还原真实主机级枚举）");
            }
        }

        /// <summary>
        /// ★ 2026-10-23（Preview.7 收口）★ 往 ViewModel 的 <c>Shares</c> 里放一条**已勾选**的共享根。
        ///
        /// 为什么必须这么做：空闲探测的目标集合来自 <c>SelectedShareRoots()</c>（只取 IsSelected），
        /// 一条都没有时会退回主机级 <c>ShareCountProbe</c> —— 于是"IPC$ 失败但指定共享持续可读、
        /// 空闲超 60 秒仍绿"这条核心场景根本跑不到共享级路径。这里补齐的只是**输入目标清单**；
        /// 探测用哪种判据、失败怎么分级、证据怎么落库，全部仍由正式代码计算。
        /// </summary>
        private static void ApplyShares(MainWindow w, string spec)
        {
            var vm = w.ViewModel;
            vm.Shares.Clear();
            if (string.IsNullOrWhiteSpace(spec) || spec.Trim() == "-")
            {
                Log("shares -> (清空，探测将退回主机级枚举)");
                return;
            }
            foreach (var raw in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var unc = raw.Trim();
                if (unc.Length == 0) continue;
                var parts = unc.Trim('\\').Split('\\');
                var name = parts.Length > 1 ? parts[^1] : unc;
                vm.Shares.Add(new ShareItem
                {
                    Name = name,
                    UncPath = unc,
                    Kind = "harness",
                    IsSelected = true
                });
            }
            Log("shares -> " + string.Join(", ", vm.Shares.Select(s => s.UncPath)));
        }

        // ── 相位 / 暂停真值（B 层输入）────────────────────────────────────────

        private static void ApplyPhase(string phase)
        {
            var s = _win?.SessionForHarness;
            if (s is null) { Log("phase: Session 为空"); return; }
            if (!Enum.TryParse<JobPhase>(phase, true, out var p)) { Log("phase: 未知 " + phase); return; }
            s.HarnessSetPhase(p);
            Log($"phase -> {s.Phase}");
        }

        private static void ApplyPause(string state)
        {
            var s = _win?.SessionForHarness;
            if (s is null) { Log("pause: Session 为空"); return; }
            if (!Enum.TryParse<PauseState>(state, true, out var p)) { Log("pause: 未知 " + state); return; }
            s.HarnessSetPauseTruth(p);
            Log($"pause -> engine={s.EnginePauseState} ui={s.PauseUiState}");
        }

        // ── 全局 16 ms 泵：把正式呈现节拍"加速"到测试中（不是旁路，跑的是同一个方法）──

        private static CancellationTokenSource? _pumpCts;

        private static void ToggleGlobalPump(bool on)
        {
            _pumpCts?.Cancel();
            _pumpCts = null;
            if (!on) { Log("glob off"); return; }

            var cts = new CancellationTokenSource();
            _pumpCts = cts;
            var dispatcher = _win?.DispatcherQueue;
            Log("glob on（每 16 ms 跑一拍真实 RefreshFooterPresentation）");
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(16, cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested) break;
                    try
                    {
                        dispatcher?.TryEnqueue(() =>
                        {
                            try { _win?.HarnessPumpFooterPresentation(); } catch { }
                        });
                    }
                    catch { break; }
                }
            }, cts.Token);
        }

        // ── 状态施加：只搬运**已存在**的公开/内部事实，不新增判定 ─────────────────

        private static async Task ApplyAsync(MainWindow w, string state)
        {
            var vm = w.ViewModel;
            var s = w.SessionForHarness;

            // A 层只动视觉，不碰 ViewModel
            if (_vis == 'a')
            {
                await ApplyVisualOnlyAsync(w, state);
                return;
            }

            // ── 第一步：先立"会话上下文"（B 层判据的真实输入前置条件）────────────────
            // 生产里 Host 由连接页填好，Phase 由引擎快照推来；这里按需补一次，
            // 之后再由各状态的注入覆盖它。放在最前是因为采样器创建时会读 Host，
            // 而 CreateSenderHost 只在 Host 为空时才拦人。
            if (NeedsConnectedContext(state))
            {
                if (string.IsNullOrWhiteSpace(vm.Host)) vm.Host = HarnessHost;
                s?.HarnessSetPhase(JobPhase.Running);
                s?.HarnessSetPauseTruth(PauseState.None);
                s?.HarnessEnsureThroughput();     // 幂等；生产入口在"IsConnected 变化"事件里
            }

            switch (state)
            {
                case "T1":                                        // 未连接 ⇒ 红常亮
                    if (s is not null) { s.HarnessSetPhase(JobPhase.Created); s.HarnessSetPauseTruth(PauseState.None); }
                    vm.SyncTransferActivity(false, 0, double.NaN);
                    vm.NotifySourceLost();                        // 清空全部可达证据
                    vm.HarnessSetConnecting(false);
                    vm.Host = string.Empty;
                    break;

                case "T2":                                        // 正在连接 ⇒ 黄呼吸
                    if (s is not null) s.HarnessSetPhase(JobPhase.Created);
                    vm.HarnessSetConnecting(true);
                    break;

                case "T3":                                        // 已连接、未传输 ⇒ 绿常亮
                    if (s is not null) { s.HarnessSetPhase(JobPhase.Running); s.HarnessSetPauseTruth(PauseState.None); }
                    vm.HarnessSetConnecting(false);
                    vm.NotifyRecoveryFinished();
                    vm.NotifySourceRestored(@"\\127.0.0.1\d$");
                    vm.SyncTransferActivity(false, 0, double.NaN);
                    // 让假探针的采样器真正跑起来（生产路径：IsConnected ⇒ EnsureNetworkThroughput）
                    s?.HarnessEnsureThroughput();
                    break;

                case "T4":                                        // 已连接 + 稳定传输 ⇒ 绿呼吸（不手工压状态）
                    if (s is not null) { s.HarnessSetPhase(JobPhase.Running); s.HarnessSetPauseTruth(PauseState.None); }
                    vm.HarnessSetConnecting(false);
                    vm.NotifySourceRestored(@"\\127.0.0.1\d$");
                    s?.HarnessSetPauseTruth(PauseState.None);
                    break;

                case "T5":                                        // 传输暂停 ⇒ 绿常亮（只施加引擎暂停真值）
                    s?.HarnessSetPauseTruth(PauseState.Paused);
                    s?.HarnessSetPhase(JobPhase.Paused);
                    break;

                case "T6":                                        // 恢复传输 ⇒ 重新绿呼吸（只撤销暂停真值）
                    s?.HarnessSetPauseTruth(PauseState.None);
                    s?.HarnessSetPhase(JobPhase.Running);
                    break;

                case "T7":                                        // 传输完成 ⇒ 绿常亮
                    s?.HarnessSetPauseTruth(PauseState.None);
                    s?.HarnessSetPhase(JobPhase.Completed);
                    vm.NotifySourceRestored(@"\\127.0.0.1\d$");
                    break;

                case "T8":                                        // 断开 ⇒ 红常亮
                    s?.HarnessSetPauseTruth(PauseState.None);
                    s?.HarnessSetPhase(JobPhase.Interrupted);
                    vm.SyncTransferActivity(false, 0, double.NaN);
                    vm.NotifySourceLost();
                    break;

                case "T9":                                        // 重连中 ⇒ 黄呼吸
                    if (s is not null) { s.HarnessSetPhase(JobPhase.Created); s.HarnessSetPauseTruth(PauseState.None); }
                    vm.SyncTransferActivity(false, 0, double.NaN);
                    vm.HarnessSetConnecting(true);
                    break;

                case "T10":                                       // 300 次重复状态投影（在呼吸运行期间做）
                    await BurstAsync(w, 300, 0);
                    break;

                default:
                    Log("未知状态: " + state);
                    return;
            }

            // 真实投影（与 16 ms 节拍 / PushFooter 完全同一条路径）。
            Invoke(w);
            Log($"state {state} -> {Describe(w)}");
        }

        // ── A 层：只驱动视觉 ──────────────────────────────────────────────────

        private static async Task ApplyVisualOnlyAsync(MainWindow w, string state)
        {
            var dot = w.SourceStateDotForHarness;
            switch (state)
            {
                case "T10":
                    await BurstAsync(w, 300, 0);
                    return;
                case "T5":
                case "T7":
                    MotionDirector.ResetSourceBreathing(dot);
                    break;
                default:
                    MotionDirector.PlaySourceBreathing(dot);
                    break;
            }
            Log($"A state {state} -> {Describe(w)}");
        }

        private static async Task APumpAsync(MainWindow w, int count)
        {
            Log($"A apump {count} x 0ms（对运行中的呼吸反复请求同一动画）");
            var dot = w.SourceStateDotForHarness;
            for (var i = 0; i < count; i++)
            {
                MotionDirector.PlaySourceBreathing(dot);
                if ((i & 63) == 0) await Task.Yield();
            }
            Log("A apump done -> " + Describe(w));
        }

        // ── 高频幂等压力：直接调真实投影方法 ────────────────────────────────────

        private static async Task BurstAsync(MainWindow w, int count, int gapMs)
        {
            Log($"burst {count} x {gapMs}ms");
            for (var i = 0; i < count; i++)
            {
                Invoke(w);
                if (gapMs > 0) await Task.Delay(gapMs);
                else if ((i & 63) == 0) await Task.Yield();
            }
            Log("burst done -> " + Describe(w));
        }

        private static void Invoke(MainWindow w) => w.HarnessProjectIndicator();

        /// <summary>
        /// 【仅 Debug】B 层用的"旧电脑地址"占位符（本机回环，**不触发任何网络访问**：
        /// 假探针不解析它，通知路径也不做可达性探测）。
        /// 存在的理由：采样器创建与"创建发送端"都要求 Host 非空，这是生产的前置条件之一。
        /// </summary>
        internal const string HarnessHost = "127.0.0.1";

        /// <summary>哪些状态需要"已连接"上下文（Host / Running / 采样器）。</summary>
        private static bool NeedsConnectedContext(string state)
            => state is "T3" or "T4" or "T5" or "T6" or "T7" or "T8" or "T10";

        // ── 运行时事实串（全链路一层不漏）─────────────────────────────────────

        private static string Describe(MainWindow w)
        {
            try
            {
                var vm = w.ViewModel;
                var s = w.SessionForHarness;
                var dot = w.SourceStateDotForHarness;
                var sb = new StringBuilder();
                sb.Append("vis=").Append(_vis).Append(' ');
                sb.Append("SourceState=").Append(vm.SourceState).Append(' ');
                // ★ 2026-10-24（四态灯定义）★ 投影层真值：LampState 才是用户可见的四态，
                //   LampScope 说明这盏灯代表哪条共享，LampText 是四态文字。
                sb.Append("LampState=").Append(vm.LampState).Append(' ');
                sb.Append("LampScope=").Append(vm.ComputeLampScope()).Append(' ');
                sb.Append("LampSummary=").Append(vm.LampScopeSummary).Append(' ');
                sb.Append("ConnTransferActive=").Append(vm.IsTransferActive).Append(' ');
                if (s is not null)
                {
                    var snap = s.NetworkThroughput;
                    sb.Append("Phase=").Append(s.Phase).Append(' ');
                    sb.Append("PauseUi=").Append(s.PauseUiState).Append(' ');
                    sb.Append("IsStopping=").Append(s.IsStopping).Append(' ');
                    sb.Append("NetBps=").Append(double.IsNaN(s.NetworkReceiveBytesPerSecond)
                        ? "NaN" : s.NetworkReceiveBytesPerSecond.ToString("0")).Append(' ');
                    sb.Append("SnapEma=").Append(double.IsNaN(snap.EmaBytesPerSecond)
                        ? "NaN" : snap.EmaBytesPerSecond.ToString("0")).Append(' ');
                    sb.Append("SnapStable=").Append(double.IsNaN(snap.StableBytesPerSecond)
                        ? "NaN" : snap.StableBytesPerSecond.ToString("0")).Append(' ');
                    sb.Append("HasLive=").Append(s.HasLiveTransferActivity).Append(' ');
                    sb.Append("Sampler=").Append(s.HarnessThroughputDiagnostic()).Append(' ');
                }
                if (dot is not null)
                {
                    sb.Append("Breathing=").Append(MotionDirector.IsSourceBreathingForHarness(dot)).Append(' ');
                    sb.Append("ElOp=").Append(dot.Opacity.ToString("0.###")).Append(' ');
                    if (dot.Fill is Microsoft.UI.Xaml.Media.SolidColorBrush b)
                    {
                        sb.Append("Fill=#")
                          .Append(b.Color.R.ToString("X2"))
                          .Append(b.Color.G.ToString("X2"))
                          .Append(b.Color.B.ToString("X2")).Append(' ');
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { return "Describe 失败: " + ex.Message; }
        }

        // ── 定点读数：把"指示灯此刻的真实运行时事实"写进日志 ─────────────────────

        private static void LogProbe(MainWindow w, string label)
        {
            try
            {
                var vm = w.ViewModel;
                var dot = w.SourceStateDotForHarness;
                var sb = new StringBuilder();
                sb.Append("probe [").Append(label).Append("] ");
                sb.Append(Describe(w)).Append(' ');
                sb.Append("Detail=\"").Append(vm.SourceStateDetail).Append("\" ");
                if (dot is not null)
                {
                    sb.Append("AutoName=\"").Append(
                        Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(dot)).Append("\" ");
                    var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(dot);
                    sb.Append("VisualOpacity=").Append(visual.Opacity.ToString("0.###")).Append(' ');
                    // 真实启动次数：幂等闸门每放行一次 +1。用来客观判定"相位有没有被重置"。
                    sb.Append("Starts=").Append(MotionDirector.SourceBreathStartCountForHarness);
                }
                Log(sb.ToString());
            }
            catch (Exception ex)
            {
                Log("probe 失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── 现场读数（写日志，用于与录屏互相印证；不作为视觉证据替代）─────────────

        private static void Banner(MainWindow w, string text)
        {
            try
            {
                // 2026-10-22 编译期隔离：XAML 里的测试横幅**已整段删除**，不再有
                // 任何只在测试里显形的界面元素。对时标记只写窗口标题——它不改变
                // 任何业务呈现，也不会在正式产物里被任何人打开。
                w.Title = "PCMig UI-HARNESS · " + text;
            }
            catch { /* 标题只是定位辅助 */ }
        }

        private static int ParseInt(string[] parts, int index, int fallback)
            => index < parts.Length && int.TryParse(parts[index], out var v) ? v : fallback;

        private static string? _logPath;
        private static void Log(string message)
        {
            try
            {
                _logPath ??= Path.Combine(AppContext.BaseDirectory, "ui-harness.log");
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { /* 日志写不进去不影响驱动 */ }
        }
    }
}


using PCMig.Core.Diagnostics;
using PCMig.Core.Models;
using PCMig.Core.State;
using PCMig.Diagnostics.Abstractions;
using PCMig.Diagnostics.Abstractions.Events;
using PCMig.Diagnostics.Abstractions.Payloads;
using Serilog;

namespace PCMig.Core.Jobs;

/// <summary>一个已落盘 Job 的上下文：定义、计划、状态、目录约定全部集中于此。</summary>
public sealed class JobContext
{
    public required string JobDir { get; init; }
    public required JobDefinition Definition { get; set; }
    public MigrationPlan? Plan { get; set; }

    public string JobId => Definition.JobId;

    /// <summary>
    /// 诊断上下文（含 Session + Job 归属）。装配根安装 sink 后由创建点显式注入；
    /// 未安装时是 NoOp 上下文（空会话）——**绝不"猜当前任务"**，任何归属都必须显式传入。
    /// </summary>
    public DiagnosticContext Diagnostics { get; set; } = CoreDiagnostics.ContextFor("JobContext");
    public string JobJsonPath => Path.Combine(JobDir, "job.json");
    public string ObservedStatePath => Path.Combine(JobDir, "observed-state.json");
    public string PlanPath => Path.Combine(JobDir, "plan.json");
    public string StatePath => Path.Combine(JobDir, "job-state.json");
    public string PreflightPath => Path.Combine(JobDir, "preflight.json");
    public string VerifyPath => Path.Combine(JobDir, "verify-report.json");
    public string ReceiptsDir => Path.Combine(JobDir, "receipts");
    public string LogsDir => Path.Combine(JobDir, "logs");
    public string RoboLogsDir => Path.Combine(JobDir, "logs", "robocopy");
    public string ReportDir => Path.Combine(JobDir, "report");
    public string PauseRequestPath => Path.Combine(JobDir, "pause.request");

    public void EnsureDirs()
    {
        Directory.CreateDirectory(ReceiptsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RoboLogsDir);
        Directory.CreateDirectory(ReportDir);
    }

    /// <summary>状态文件存在但解析失败 = 损坏（区别于"文件不存在"的合法新任务）。全量测试实测：损坏被静默当成 Created 0%，误导用户。</summary>
    public bool StateFileCorrupted =>
        File.Exists(StatePath) && !(JsonStateStore.TryRead<JobState>(StatePath, out var s) && s != null);

    /// <summary>回执目录里是否已经有凭证（说明这个任务确实跑过一次并留下了权威记录）。</summary>
    public bool HasReceipts => HasAnyReceipt(ReceiptsDir);

    private static bool HasAnyReceipt(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any(); }
        catch { return false; }
    }

    /// <summary>
    /// 视图不可信（T14/O6）：状态文件损坏（非法 JSON / 空文件），或状态文件被删除但回执还在
    /// （任务确实跑过——正常任务的状态文件在创建时就已落盘，不会无故缺失）。
    /// 此时"默认空状态"的 0.0% / 对象 0 **绝不能**当作真实进度显示，必须明确写成"待由回执重建"。
    /// </summary>
    public bool StateViewUnreliable =>
        StateFileCorrupted || (!File.Exists(StatePath) && HasReceipts);

    /// <summary>job.lock 是否被存活进程独占持有（进程死亡后 OS 自动释放）。</summary>
    public bool HasLiveHolder => JobManager.IsLocked(JobDir);

    public JobState LoadStateOrNew()
    {
        if (JsonStateStore.TryRead<JobState>(StatePath, out var s) && s != null) return s;
        return new JobState { JobId = JobId, Phase = JobPhase.Created };
    }

    public void SaveState(JobState state)
    {
        state.LastUpdateUtc = DateTime.UtcNow;
        JsonStateStore.WriteAtomic(StatePath, state, "JobState", Diagnostics);
    }

    /// <summary>把任务定义写回 job.json（v0.3.8：用户显式确认"扫描残缺仍继续"等决定要留档并可审计）。</summary>
    public void SaveDefinition() => JsonStateStore.WriteAtomic(JobJsonPath, Definition, "JobDefinition", Diagnostics);

    public void SavePlan(MigrationPlan plan) { Plan = plan; JsonStateStore.WriteAtomic(PlanPath, plan, "Plan", Diagnostics); }
    public void SaveObserved(ObservedState o) => JsonStateStore.WriteAtomic(ObservedStatePath, o, "ObservedState", Diagnostics);
    public void SavePreflight(PreflightReport p) => JsonStateStore.WriteAtomic(PreflightPath, p, "PreflightReport", Diagnostics);
    public void SaveVerify(VerifyReport v) => JsonStateStore.WriteAtomic(VerifyPath, v, "VerifyReport", Diagnostics);

    /// <summary>读取上次验证报告（不存在或损坏返回 null）。「尝试修复」用它确定要重拷哪些对象。</summary>
    public VerifyReport? LoadVerify() =>
        JsonStateStore.TryRead<VerifyReport>(VerifyPath, out var v) ? v : null;

    /// <summary>
    /// 写一份对象回执。
    /// v0.5.3：文件名改为**同名不覆盖**——同一对象在同一秒内写两次（重试/恢复可能触发）时，
    /// 追加 <c>-1</c>、<c>-2</c> 后缀而不是让 <see cref="JsonStateStore.WriteAtomic"/> 静默覆盖掉上一趟。
    /// 读取端只枚举 <c>*.json</c>、从不解析文件名 ⇒ 历史回执（含 v0.5.0/v0.5.1 与预览版）原样可读。
    /// </summary>
    public void SaveReceipt(ObjectReceipt receipt)
    {
        var name = $"{receipt.ObjectId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var path = Path.Combine(ReceiptsDir, name + ".json");
        for (var i = 1; File.Exists(path) && i <= MaxSameSecondReceiptSuffix; i++)
        {
            path = Path.Combine(ReceiptsDir, $"{name}-{i}.json");
        }

        JsonStateStore.WriteAtomic(path, receipt, "Receipt", Diagnostics.WithObject(receipt.ObjectId));
    }

    /// <summary>同秒同名回执的后缀上限（99 足够：同一对象在同一秒内写 100 份回执不是正常现场）。</summary>
    private const int MaxSameSecondReceiptSuffix = 99;

    public List<ObjectReceipt> LoadReceipts(Serilog.ILogger? log = null) =>
        JsonStateStore.ReadAllReceipts<ObjectReceipt>(ReceiptsDir,
            corrupt => log?.Warning("损坏的 Receipt 已跳过: {Detail}", corrupt));

    public string? ReadPauseRequest()
    {
        try { return File.Exists(PauseRequestPath) ? File.ReadAllText(PauseRequestPath).Trim() : null; }
        catch { return null; }
    }

    /// <summary>
    /// 写暂停请求文件。**观察但不改语义**：写入失败仍然照原样抛出（调用方本来就要看到失败）。
    /// 这条事件是"点了暂停没反应"的第一手事实（文件写成功 ≠ 引擎已观察到）。
    /// </summary>
    public void RequestPause(bool immediate)
    {
        var ok = false;
        string? reason = null;
        try
        {
            File.WriteAllText(PauseRequestPath, immediate ? "Immediate" : "Cooperative");
            ok = true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            throw;
        }
        finally
        {
            PublishPauseRequest(immediate, ok, reason, PauseRequestCleared: false);
        }
    }

    /// <summary>
    /// 清除暂停请求。
    ///
    /// ★ D6.3 §11（缺口③收口）★ 业务语义**不变**（无论文件在不在都往下走、不抛），
    /// 但观察口径必须与事实一致："文件本来就不存在"**不是**"已清除"。
    /// 原实现无条件写 `Succeeded`，于是包内永远无法回答"当时到底有没有暂停请求、有没有真的删掉"：
    ///   · 文件存在且删除成功 ⇒ Succeeded（requestExisted=true / deleted=true）；
    ///   · 文件本来不存在     ⇒ Skipped  （requestExisted=false / deleted=false，reasonCode=pause-request-absent）；
    ///   · 删除失败           ⇒ Failed   （requestExisted=true / deleted=false，reasonCode=异常类型）。
    /// </summary>
    public void ClearPauseRequest()
    {
        var existed = false;
        var deleted = false;
        string? failure = null;
        try
        {
            existed = File.Exists(PauseRequestPath);
            if (existed)
            {
                File.Delete(PauseRequestPath);
                deleted = true;
            }
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name;
        }

        PublishPauseRequest(
            immediate: false,
            succeeded: failure is null,
            reasonCode: failure ?? (existed ? null : "pause-request-absent"),
            PauseRequestCleared: true,
            requestExisted: existed,
            deleted: deleted);
    }

    private void PublishPauseRequest(
        bool immediate,
        bool succeeded,
        string? reasonCode,
        bool PauseRequestCleared,
        bool? requestExisted = null,
        bool? deleted = null)
    {
        try
        {
            var diagnostics = CoreDiagnostics.Sink.Publisher;
            var descriptor = PauseRequestCleared ? TransferEvents.PauseRequestCleared : TransferEvents.PauseRequestWriteResult;
            if (!diagnostics.IsEnabledFor(descriptor)) return;

            // ★ D6.3 §11（缺口③）★ 终点由事实决定：没抛异常但**本来就没有暂停请求** ⇒ Skipped（不是 Succeeded）。
            var outcome = !succeeded
                ? DiagnosticOutcome.Failed
                : PauseRequestCleared && requestExisted == false
                    ? DiagnosticOutcome.Skipped
                    : DiagnosticOutcome.Succeeded;

            diagnostics.TryPublish(new DiagnosticEventDraft(
                descriptor,
                Diagnostics,
                new TrnPauseRequestPayload(immediate, succeeded, reasonCode, requestExisted, deleted),
                Level: outcome == DiagnosticOutcome.Failed ? DiagnosticLevel.Warning : DiagnosticLevel.Information,
                Outcome: outcome,
                Path: CoreDiagnostics.Sink.PathRef(PauseRequestPath, PathRole.JobDir, "job")));
        }
        catch (Exception)
        {
            // 观察失败绝不改变暂停/恢复语义。
        }
    }
}

public sealed class JobSummary
{
    public string JobId { get; set; } = "";
    public string JobDir { get; set; } = "";
    /// <summary>job-state.json 里如实记录的阶段（存档内容，不做任何改写）。</summary>
    public JobPhase Phase { get; set; }
    public string SourceHost { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public DateTime LastUpdateUtc { get; set; }
    public double Percent { get; set; }

    /// <summary>job.lock 是否被存活进程持有（陈旧 running 的判据，复用既有 Job Lock）。</summary>
    public bool LiveHolder { get; set; }
    /// <summary>回执目录里已有凭证（任务确实跑过）。</summary>
    public bool HasReceipts { get; set; }
    /// <summary>状态文件损坏/缺失但回执在 → 进度不可信。</summary>
    public bool StateUnreliable { get; set; }

    /// <summary>显示用阶段：陈旧 running/awaitingReview（无存活持有者）按"已中断（可续传）"呈现。</summary>
    public JobPhase DisplayPhase => PhaseView.Effective(Phase, LiveHolder, HasReceipts);

    /// <summary>阶段显示文案（CLI 与界面共用同一句话，避免两处说法不一致）。</summary>
    public string PhaseText => PhaseView.Describe(Phase, LiveHolder, HasReceipts);

    /// <summary>列表里给"进度不可信"补一句实话，避免脚本/人把 0.0% 当真。</summary>
    public string DisplayNote => StateUnreliable
        ? "｜⚠ 状态文件损坏或缺失，进度不可信（回执为准；resume 会按回执重建）" : "";
}

/// <summary>
/// Job 管理器：创建/打开/枚举 Job。
/// Job 目录默认位于 %ProgramData%\PCMig\Jobs\JOB-*，可用环境变量 PCMIG_JOBS 覆盖（便携场景）。
/// </summary>
public sealed class JobManager
{
    private readonly ILogger _log;

    public JobManager(ILogger log) { _log = log.ForContext<JobManager>(); }

    public static string JobsRoot =>
        Environment.GetEnvironmentVariable("PCMIG_JOBS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCMig", "Jobs");

    public static string NewJobId()
        => $"JOB-{DateTime.Now:yyyyMMdd}-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";

    public JobContext Create(JobDefinition def)
    {
        if (string.IsNullOrWhiteSpace(def.JobId)) def.JobId = NewJobId();
        def.CreatedUtc = DateTime.UtcNow;
        var ctx = new JobContext
        {
            JobDir = Path.Combine(JobsRoot, def.JobId),
            Definition = def
        };
        ctx.EnsureDirs();
        ctx.Diagnostics = CoreDiagnostics.ContextFor("JobManager", def.JobId);
        JsonStateStore.WriteAtomic(ctx.JobJsonPath, def, "JobDefinition", ctx.Diagnostics);
        ctx.SaveState(new JobState { JobId = def.JobId, Phase = JobPhase.Created });
        _log.Information("Job 已创建: {JobId} @ {Dir}", def.JobId, ctx.JobDir);
        return ctx;
    }

    /// <summary>按 JobId 或直接按目录打开 Job。</summary>
    public JobContext Open(string jobIdOrDir)
    {
        var dir = Directory.Exists(jobIdOrDir) ? jobIdOrDir : Path.Combine(JobsRoot, jobIdOrDir);
        var jobJson = Path.Combine(dir, "job.json");
        if (!File.Exists(jobJson))
            throw new FileNotFoundException($"找不到 Job 定义: {jobJson}（可用 pcmig list 查看全部任务）");
        JobDefinition def;
        try
        {
            def = JsonStateStore.Read<JobDefinition>(jobJson);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"任务定义文件已损坏（可能是异常断电写坏）：{jobJson}。\n可删除该任务目录后用相同参数重新创建；已复制的数据不会丢，新任务会自动增量续接。", ex);
        }
        var ctx = new JobContext { JobDir = dir, Definition = def };
        ctx.EnsureDirs();
        if (JsonStateStore.TryRead<MigrationPlan>(ctx.PlanPath, out var plan)) ctx.Plan = plan;
        return ctx;
    }

    /// <summary>可续传的未完成阶段。</summary>
    private static readonly JobPhase[] ResumablePhases =
        [JobPhase.Running, JobPhase.Paused, JobPhase.Interrupted, JobPhase.AwaitingReview, JobPhase.CompletedWithErrors];

    /// <summary>
    /// 查找与指定源（host，可选 targetRoot）匹配、且未被存活进程锁定的未完成任务。
    /// 用于"重开工具后主动提醒是否续传"。
    /// </summary>
    public List<JobSummary> FindUnfinished(string host, string? targetRoot)
    {
        return ListAll().Where(j =>
                ResumablePhases.Contains(j.Phase) &&
                string.Equals(j.SourceHost, host, StringComparison.OrdinalIgnoreCase) &&
                (targetRoot == null || string.Equals(
                    j.TargetRoot.TrimEnd('\\', '/'), targetRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            .Where(j => !IsLocked(j.JobDir))
            .ToList();
    }

    /// <summary>Job 是否正被存活进程持有（job.lock 被独占）。进程死亡后 OS 自动释放文件锁。</summary>
    public static bool IsLocked(string jobDir)
    {
        var p = Path.Combine(jobDir, "job.lock");
        if (!File.Exists(p)) return false;
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // 能独占打开 = 无人持有
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public List<JobSummary> ListAll()
    {
        var list = new List<JobSummary>();
        if (!Directory.Exists(JobsRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(JobsRoot, "JOB-*"))
        {
            var jobJson = Path.Combine(dir, "job.json");
            if (!File.Exists(jobJson)) continue;
            try
            {
                var def = JsonStateStore.Read<JobDefinition>(jobJson);
                var jobStatePath = Path.Combine(dir, "job-state.json");
                var hasStateFile = File.Exists(jobStatePath);
                var stateReadable = JsonStateStore.TryRead<JobState>(jobStatePath, out var s) && s != null;
                // ★ C03 真机硬断电（2026-10-04）★ 状态文件**存在但读不出来** = 上一次运行在写状态的途中被硬断电带走
                //   （现场：NTFS 保住了文件长度，内容是整片 0x00 —— 见 JsonStateStore.WriteAtomic 无 FlushFileBuffers）。
                //   这是「被中断的任务」，不是「刚建好的任务」：必须按 Interrupted 呈现，否则
                //   FindUnfinished 的 ResumablePhases 过滤会在这里把它丢掉，用户在 UI 上既看不到那个被中断的旧任务、
                //   也拿不到 Resume/Verify/Repair —— 而目标盘上真实躺着半成品数据。
                //   只有文件**根本不存在**时才保留枚举默认值 Created（从未写过状态，不构成可续传任务）。
                var state = stateReadable
                    ? s!
                    : new JobState { JobId = def.JobId, Phase = hasStateFile ? JobPhase.Interrupted : JobPhase.Created };
                var hasReceipts = Directory.Exists(Path.Combine(dir, "receipts"))
                    && Directory.EnumerateFiles(Path.Combine(dir, "receipts"), "*.json").Any();
                var corrupted = hasStateFile && !stateReadable;
                list.Add(new JobSummary
                {
                    JobId = def.JobId,
                    JobDir = dir,
                    Phase = state.Phase,
                    SourceHost = def.SourceHost,
                    TargetRoot = def.TargetRoot,
                    LastUpdateUtc = state.LastUpdateUtc,
                    Percent = state.Percent,
                    LiveHolder = IsLocked(dir),
                    HasReceipts = hasReceipts,
                    StateUnreliable = corrupted || (!File.Exists(jobStatePath) && hasReceipts)
                });
            }
            catch (Exception ex) { _log.Warning(ex, "读取 Job 失败: {Dir}", dir); }
        }
        return list.OrderByDescending(x => x.LastUpdateUtc).ToList();
    }
}

/// <summary>
/// 阶段视图判定（T05-O1 / D1 回归遗留）——只影响"怎么显示"，**不改存档**：
/// job-state.json 里的阶段照旧原样保存，这里只回答"这个阶段现在该怎么说"。
///
/// 判据复用既有的 Job Lock（job.lock 独占性，进程死亡后由 OS 自动释放），不新造任何存活标记文件：
///   - Running 而锁无人持有 = 进程已退出（硬杀 / 崩溃 / 被任务管理器结束）→ 陈旧 running，按"已中断（可续传）"显示；
///   - AwaitingReview 而锁无人持有 **且已有回执** = 确实跑过（run 开头那次 SaveState(Running) 恰好被外部占用跳过）
///     → 同样是陈旧视图；正常流程的 AwaitingReview（刚生成计划、一个回执都没有）不算陈旧，照原样显示。
/// 大测试实测：硬杀后 status 显示 running 而机器上根本没有 PCMig 进程，脚本会误判"任务还在跑"。
/// </summary>
public static class PhaseView
{
    public static bool IsStale(JobPhase stored, bool hasLiveHolder, bool hasReceipts)
    {
        if (hasLiveHolder) return false;
        return stored switch
        {
            JobPhase.Running => true,
            JobPhase.AwaitingReview => hasReceipts,
            _ => false
        };
    }

    /// <summary>显示用阶段：陈旧阶段按"已中断（可续传）"呈现，其余原样返回。</summary>
    public static JobPhase Effective(JobPhase stored, bool hasLiveHolder, bool hasReceipts)
        => IsStale(stored, hasLiveHolder, hasReceipts) ? JobPhase.Interrupted : stored;

    /// <summary>阶段显示文案（CLI status/list 与 GUI 共用，保证两处说法一致）。</summary>
    public static string Describe(JobPhase stored, bool hasLiveHolder, bool hasReceipts)
    {
        if (IsStale(stored, hasLiveHolder, hasReceipts))
            return $"Interrupted（已中断，可续传；job-state.json 里记的是 {stored}，但没有任何进程持有 job.lock）";
        return stored switch
        {
            JobPhase.Running => "Running（正在传输）",
            JobPhase.AwaitingReview => "AwaitingReview（计划已生成，等待确认执行）",
            _ => stored.ToString()
        };
    }
}

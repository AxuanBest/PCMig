namespace PCMig.Diagnostics.Abstractions;

/// <summary>
/// 不可变诊断上下文：一个观察点"属于谁"的全部身份。**不携带时间/序号**——那两个字段由 runtime
/// 在发布时统一盖戳（见 IDiagnosticPublisher），避免调用点各写各的时钟。
///
/// 关键纪律（架构方案 §8）：
///   · 必须显式传到跨线程回调/长期任务；AsyncLocal 只是便捷 scope，不是唯一事实源；
///   · dispose 不能让晚到事件误挂到下一个动作 ⇒ 拿到的 context 是不可变的，禁止改写共享实例；
///   · 每次 Run/Resume 必须换新的 OperationId 与 RunGeneration，防止上一次运行的迟到事件污染本次。
/// </summary>
public sealed record DiagnosticContext
{
    public required Guid SessionId { get; init; }

    /// <summary>业务任务 ID（原样沿用 job.json 里的 JobId，不改存档）。</summary>
    public string? JobId { get; init; }

    /// <summary>业务对象 ID（必须与 JobId 联合使用）。</summary>
    public string? ObjectId { get; init; }

    /// <summary>一次语义交互（用户点了一下）。</summary>
    public Guid? ActionId { get; init; }

    /// <summary>一次操作（一次 Run / 一次 Verify / 一次 Export）。</summary>
    public Guid? OperationId { get; init; }

    /// <summary>父操作（Persistence/Verify/Repair 挂在某个 Transfer 之下时使用）。</summary>
    public Guid? ParentOperationId { get; init; }

    /// <summary>本次运行的代际序号：同一 Job 的第 N 次 Run。用于隔离旧运行的迟到投影。</summary>
    public long RunGeneration { get; init; }

    /// <summary>对象级尝试次数（robocopy 自动重试时递增；用户重试是新的 Action+Operation）。</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>通道 token，取值见 <see cref="DiagnosticPass"/>。</summary>
    public string? Pass { get; init; }

    /// <summary>稳定控件 ID（见 ControlId registry），例如 Step1.Connect。</summary>
    public string? ControlId { get; init; }

    /// <summary>组件名（稳定短名，例如 ConnectionViewModel / RobocopyRunner）。</summary>
    public string? Component { get; init; }

    /// <summary>根上下文：每个进程启动一个新 SessionId。</summary>
    public static DiagnosticContext Root(Guid sessionId, string? component = null) => new()
    {
        SessionId = sessionId,
        Component = component,
    };

    public DiagnosticContext WithComponent(string? component) => this with { Component = component };

    public DiagnosticContext WithJob(string? jobId) => this with { JobId = jobId };

    public DiagnosticContext WithObject(string? objectId) => this with { ObjectId = objectId };

    public DiagnosticContext WithControl(string? controlId) => this with { ControlId = controlId };

    /// <summary>开一个新的语义交互作用域（保留 Job/Operation 归属）。</summary>
    public DiagnosticContext WithAction(Guid actionId) => this with { ActionId = actionId };

    /// <summary>
    /// 开一个新的操作作用域。**换 Operation 必须同时给出新的 RunGeneration**，
    /// 否则旧运行的迟到事件会被误判为本次运行（"Run A 迟到事件到达 Run B"反例）。
    /// </summary>
    public DiagnosticContext WithOperation(Guid operationId, Guid? parentOperationId = null, long? runGeneration = null) =>
        this with
        {
            OperationId = operationId,
            ParentOperationId = parentOperationId,
            RunGeneration = runGeneration ?? RunGeneration,
            Attempt = 1,
        };

    /// <summary>递增尝试次数（自动重试）；OperationId 保持不变。</summary>
    public DiagnosticContext WithAttempt(int attempt) => this with { Attempt = attempt < 1 ? 1 : attempt };

    public DiagnosticContext WithPass(string? pass) => this with { Pass = pass };
}
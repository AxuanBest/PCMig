using System.Text.Json;

namespace PCMig.Diagnostics.Abstractions.Payloads;

// ────────────────────────── NET：连接链路的原始网络事实 ──────────────────────────
// 纪律：这些只回答"某个 API 这样返回了"，**不回答**"远端关机了/网络断了"（那是候选原因，不是事实）。

/// <summary>NET.ProbeStarted：到达探测前的目标描述（主机名/IP 已别名化）。</summary>
public sealed record NetProbeStartedPayload(string TargetAlias, bool IsLiteralAddress, int SourcePathCount) : IDiagnosticPayload
{
    public const string Name = "NetProbeStarted";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("targetAlias", TargetAlias);
        w.WriteBoolean("isLiteralAddress", IsLiteralAddress);
        w.WriteNumber("sourcePathCount", SourcePathCount);
    }
}

/// <summary>NET.DnsResolved / NET.DnsFailed：名字解析结果（地址个数，不含地址明文）。</summary>
public sealed record NetDnsResultPayload(bool Resolved, int AddressCount, long ElapsedMs, string? FailureKind) : IDiagnosticPayload
{
    public const string Name = "NetDnsResult";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteBoolean("resolved", Resolved);
        w.WriteNumber("addressCount", AddressCount);
        w.WriteNumber("elapsedMs", ElapsedMs);
        PayloadJson.WriteStringOrNull(w, "failureKind", FailureKind);
    }
}

/// <summary>NET.TcpProbeAttempt / NET.TcpProbeFailed：逐个地址的 445 探测（地址只给序号）。</summary>
public sealed record NetTcpProbePayload(int AddressIndex, long ElapsedMs, bool Succeeded) : IDiagnosticPayload
{
    public const string Name = "NetTcpProbe";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("addressIndex", AddressIndex);
        w.WriteNumber("elapsedMs", ElapsedMs);
        w.WriteBoolean("succeeded", Succeeded);
    }
}

/// <summary>SMB 会话建立/复用（api 名说明是哪条路径：IPC$ / 直连共享）。</summary>
public sealed record NetSmbSessionPayload(string Api, bool Reused, long ElapsedMs, bool Succeeded) : IDiagnosticPayload
{
    public const string Name = "NetSmbSession";
    public string PayloadName => Name;

    /// <summary>★ D6.1 §16 ★ 原生返回码（0 = 成功）。用它才能把"连不上"与"连上了但对方不接受"分开。</summary>
    public int Win32Error { get; init; }

    /// <summary>本次是否使用了**显式凭据**（只记语义，<b>绝不</b>记用户名/口令）。</summary>
    public bool UsedExplicitCreds { get; init; }

    /// <summary>稳定原因 token（connect-requested / existing-session / credential-conflict-reuse / …）。</summary>
    public string? ReasonCode { get; init; }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("api", Api);
        w.WriteBoolean("reused", Reused);
        w.WriteNumber("elapsedMs", ElapsedMs);
        w.WriteBoolean("succeeded", Succeeded);
        w.WriteNumber("win32Error", Win32Error);
        w.WriteBoolean("usedExplicitCreds", UsedExplicitCreds);
        PayloadJson.WriteStringOrNull(w, "reasonCode", ReasonCode);
    }
}

/// <summary>共享发现（枚举成功/被拦截、直接探测到的管理共享个数）。</summary>
public sealed record NetShareDiscoveryPayload(bool EnumerationSucceeded, int ShareCount, bool AdminSharesProbed) : IDiagnosticPayload
{
    public const string Name = "NetShareDiscovery";
    public string PayloadName => Name;

    /// <summary>★ D6.1 §16 ★ 结果里包含多少个管理共享（E$ 这类），以及用了哪一级枚举。</summary>
    public int AdminShareCount { get; init; }

    public string? Level { get; init; }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteBoolean("enumerationSucceeded", EnumerationSucceeded);
        w.WriteNumber("shareCount", ShareCount);
        w.WriteBoolean("adminSharesProbed", AdminSharesProbed);
        w.WriteNumber("adminShareCount", AdminShareCount);
        PayloadJson.WriteStringOrNull(w, "level", Level);
    }
}

/// <summary>NET.CredentialProofObserved：凭据链路被证明可用（例如错误 67 = 认证通过但不导出 IPC$）。</summary>
public sealed record NetCredentialProofPayload(string ProofKind) : IDiagnosticPayload
{
    public const string Name = "NetCredentialProof";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w) => w.WriteString("proofKind", ProofKind);
}

// ────────────────────────── PFL：预检编排的结论级观测 ──────────────────────────

/// <summary>单项预检完成（用稳定 checkCode，不用中文检查名做判据）。</summary>
public sealed record PflCheckPayload(string CheckCode, string Severity, bool Pass) : IDiagnosticPayload
{
    public const string Name = "PflCheck";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("checkCode", CheckCode);
        w.WriteString("severity", Severity);
        w.WriteBoolean("pass", Pass);
    }
}

/// <summary>源路径探测结果（这是真正的放行闸门；IPC$/枚举只是发现手段）。</summary>
public sealed record PflSourceProbePayload(int SourceCount, int AccessibleCount, int UnavailableCount) : IDiagnosticPayload
{
    public const string Name = "PflSourceProbe";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("sourceCount", SourceCount);
        w.WriteNumber("accessibleCount", AccessibleCount);
        w.WriteNumber("unavailableCount", UnavailableCount);
    }
}

/// <summary>目标卷事实（卷类型 + 空间；这些是数字，不是个人信息）。</summary>
public sealed record PflTargetVolumePayload(string VolumeKind, long FreeBytesMb, long TotalBytesMb, bool SpaceSufficient) : IDiagnosticPayload
{
    public const string Name = "PflTargetVolume";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteString("volumeKind", VolumeKind);
        w.WriteNumber("freeBytesMb", FreeBytesMb);
        w.WriteNumber("totalBytesMb", TotalBytesMb);
        w.WriteBoolean("spaceSufficient", SpaceSufficient);
    }
}

/// <summary>链路吞吐基准（只在用户显式开启时存在；诊断不会主动跑它）。</summary>
public sealed record PflBenchmarkPayload(double ReadMBs, double WriteMBs, bool TooSlow) : IDiagnosticPayload
{
    public const string Name = "PflBenchmark";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteNumber("readMBs", Math.Round(ReadMBs, 2));
        w.WriteNumber("writeMBs", Math.Round(WriteMBs, 2));
        w.WriteBoolean("tooSlow", TooSlow);
    }
}

/// <summary>预检开始/结束的汇总（结论 + 各类检查计数）。</summary>
public sealed record PflSummaryPayload(bool OverallPass, int CheckCount, int ErrorCount, int WarningCount) : IDiagnosticPayload
{
    public const string Name = "PflSummary";
    public string PayloadName => Name;
    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteBoolean("overallPass", OverallPass);
        w.WriteNumber("checkCount", CheckCount);
        w.WriteNumber("errorCount", ErrorCount);
        w.WriteNumber("warningCount", WarningCount);
    }
}
namespace PCMig.Diagnostics.Abstractions.Events;

/// <summary>
/// NET 族（3000 段）：DNS / TCP / SMB 会话 / 共享发现的**原始网络事实**。
/// 纪律：这些只是"某个 API 这样返回了"，**不等于**"远端已关机/网络断了"。
/// </summary>
public static class NetEvents
{
    public static readonly EventDescriptor ProbeStarted = EventDescriptor.Define(
        DiagnosticCategory.Net, 1, "NET.ProbeStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "NetProbeStarted");

    public static readonly EventDescriptor DnsResolved = EventDescriptor.Define(
        DiagnosticCategory.Net, 2, "NET.DnsResolved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "NetDnsResult");

    public static readonly EventDescriptor DnsFailed = EventDescriptor.Define(
        DiagnosticCategory.Net, 3, "NET.DnsFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "NetDnsResult");

    /// <summary>逐个地址的 TCP 445 尝试（高频 ⇒ Verbose）。</summary>
    public static readonly EventDescriptor TcpProbeAttempt = EventDescriptor.Define(
        DiagnosticCategory.Net, 4, "NET.TcpProbeAttempt",
        DiagnosticLevel.Debug, DeliveryClass.Verbose, PrivacyClassification.Personal, "NetTcpProbe");

    public static readonly EventDescriptor TcpProbeFailed = EventDescriptor.Define(
        DiagnosticCategory.Net, 5, "NET.TcpProbeFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "NetTcpProbe");

    public static readonly EventDescriptor SmbSessionConnectStarted = EventDescriptor.Define(
        DiagnosticCategory.Net, 6, "NET.SmbSessionConnectStarted",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "NetSmbSession");

    public static readonly EventDescriptor SmbSessionConnected = EventDescriptor.Define(
        DiagnosticCategory.Net, 7, "NET.SmbSessionConnected",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "NetSmbSession");

    /// <summary>复用了本机既有连接（"恰好还连着"不是本系统建立的会话）。</summary>
    public static readonly EventDescriptor SmbSessionReused = EventDescriptor.Define(
        DiagnosticCategory.Net, 8, "NET.SmbSessionReused",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "NetSmbSession");

    public static readonly EventDescriptor SmbConnectFailed = EventDescriptor.Define(
        DiagnosticCategory.Net, 9, "NET.SmbConnectFailed",
        DiagnosticLevel.Error, DeliveryClass.Operational, PrivacyClassification.Personal, "NetSmbSession");

    public static readonly EventDescriptor ShareEnumerationFailed = EventDescriptor.Define(
        DiagnosticCategory.Net, 10, "NET.ShareEnumerationFailed",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Personal, "NetShareDiscovery");

    public static readonly EventDescriptor ShareResolved = EventDescriptor.Define(
        DiagnosticCategory.Net, 11, "NET.ShareResolved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "NetShareDiscovery");

    /// <summary>纯错误码观测（无路径/主机明文 ⇒ Public）。</summary>
    public static readonly EventDescriptor NativeErrorObserved = EventDescriptor.Define(
        DiagnosticCategory.Net, 12, "NET.NativeErrorObserved",
        DiagnosticLevel.Warning, DeliveryClass.Operational, PrivacyClassification.Public);

    /// <summary>凭据链路已证明可用（例如错误 67 = 认证通过但对方不导出 IPC$）。</summary>
    public static readonly EventDescriptor CredentialProofObserved = EventDescriptor.Define(
        DiagnosticCategory.Net, 13, "NET.CredentialProofObserved",
        DiagnosticLevel.Information, DeliveryClass.Operational, PrivacyClassification.Personal, "NetCredentialProof");

    internal static readonly EventDescriptor[] All =
    {
        ProbeStarted, DnsResolved, DnsFailed, TcpProbeAttempt, TcpProbeFailed,
        SmbSessionConnectStarted, SmbSessionConnected, SmbSessionReused, SmbConnectFailed,
        ShareEnumerationFailed, ShareResolved, NativeErrorObserved, CredentialProofObserved,
    };
}
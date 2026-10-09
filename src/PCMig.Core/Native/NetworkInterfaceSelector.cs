using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCMig.Core.Native;

/// <summary>选接口的结局。除 <see cref="Selected"/> 之外一律表示"选不出可靠接口"——按任务要求：选不出来就如实报告不可用，不得乱选一块网卡冒充。</summary>
public enum InterfaceSelectionOutcome
{
    /// <summary>成功定位到访问目标所在网络的那块本机网卡。</summary>
    Selected = 0,
    /// <summary>没有可用的目标地址（DNS 没解析出来 / 调用方没给）。</summary>
    NoEndpoint,
    /// <summary>地址族不是 IPv4/IPv6。</summary>
    UnsupportedAddressFamily,
    /// <summary>目标就是本机（回环）：本机自我迁移没有真实网卡吞吐可言。</summary>
    Loopback,
    /// <summary>IPv6 link-local 缺 scope id（fe80:: 不带 %接口）：系统会在多条 fe80::/64 路由里任挑一条，结果不可信。</summary>
    LinkLocalWithoutScope,
    /// <summary>本机没有任何接口拥有目标所在子网的地址（可能没连上旧电脑的网络，或跨了路由器）。</summary>
    NoMatchingInterface,
    /// <summary>同一子网有多块网卡并列最长前缀，且系统选路也定不下来：属实歧义，宁可不可用。</summary>
    Ambiguous,
    /// <summary>IP Helper 选路失败（网络不可达 / 主机不可达 / 参数非法）。</summary>
    RoutingFailed,
    /// <summary>选中了索引，但读不到该接口的行信息（网卡刚被拔掉 / 布局自检未通过 / 系统过旧）。</summary>
    RowReadFailed,
    /// <summary>本机不是 Windows，或缺少 iphlpapi。</summary>
    PlatformUnavailable,
}

/// <summary>
/// 一次接口选择的结果（纯数据 + 中文理由，便于日志与界面如实说明"读的是哪块网卡、为什么选它/为什么选不出"）。
/// </summary>
/// <param name="Outcome">结局。</param>
/// <param name="InterfaceIndex">系统接口索引；仅 <see cref="InterfaceSelectionOutcome.Selected"/> 时非 0。</param>
/// <param name="Description">网卡描述（iphlpapi 口径）。</param>
/// <param name="Alias">网卡别名（用户看到的连接名）。</param>
/// <param name="InterfaceType">IFTYPE（6=以太网，71=无线）。</param>
/// <param name="ReceiveLinkSpeed">协商接收链路速率（bit/s，可能为 0）。</param>
/// <param name="Reason">中文理由（含"为什么"，用于界面提示与日志）。</param>
/// <param name="Source">选它的依据：IPv6Scope / AddressPrefix / BestInterface。</param>
public sealed record InterfaceSelection(
    InterfaceSelectionOutcome Outcome,
    uint InterfaceIndex,
    string Description,
    string Alias,
    uint InterfaceType,
    ulong ReceiveLinkSpeed,
    string Reason,
    string Source)
{
    /// <summary>是否拿到了可用的网卡索引（只有这一种情况才允许去读 InOctets）。</summary>
    public bool IsUsable => Outcome == InterfaceSelectionOutcome.Selected;

    internal static InterfaceSelection Ok(uint index, in InterfaceRowInfo row, string source, string reason) =>
        new(InterfaceSelectionOutcome.Selected, index, row.Description, row.Alias, row.Type, row.ReceiveLinkSpeed, reason, source);

    internal static InterfaceSelection No(InterfaceSelectionOutcome outcome, string reason) =>
        new(outcome, 0, "", "", 0, 0, reason, "");
}

/// <summary>
/// InterfaceIndex 选择器：按"实际访问旧电脑的网络路径"挑出本机对应的网卡索引。
///
/// <para>
/// 【为什么必须选对】真实接收吞吐只能读"流量实际经过的那块网卡"的计数器：
/// 选错网卡 ⇒ 读数恒为 0（或读到别的业务的流量），界面就会给出假的 0 B/s 或假的 ETA。
/// 所以这里的原则是**宁可报不可用，也不乱选**。
/// </para>
///
/// <para>选择顺序（每一步都有明确依据，失败继续下一步，全失败则如实报告结局）：</para>
/// <list type="number">
///   <item><b>IPv6 scope</b>：形如 <c>fe80::1%12</c> 的地址，<c>%</c> 后面的 scope id 在 Windows 上就是接口索引
///         （IPv6 的 scope id 必须被正确处理，不能只支持 IPv4 写法）——直接按它取行信息校验；</item>
///   <item><b>同子网地址前缀匹配</b>：遍历本机 <see cref="NetworkInterface"/> 的单播地址，
///         与目标同族、同子网（按掩码前缀逐位比较）、优先 <c>OperationalStatus.Up</c>，
///         取**最长前缀**；并列最长（例如实验机上多块网卡在同一网段）时不擅自挑一块；</item>
///   <item><b>系统选路</b>：<see cref="IpHelperIfEntry.TryGetBestInterfaceIndex"/>（GetBestInterfaceEx），
///         即 Windows 自己的路由表结论，比我们的猜测权威；</item>
///   <item>以上都失败 ⇒ <see cref="InterfaceSelectionOutcome.Ambiguous"/> /
///         <see cref="InterfaceSelectionOutcome.NoMatchingInterface"/> /
///         <see cref="InterfaceSelectionOutcome.RoutingFailed"/>，读数一律按"不可用"处理。</item>
/// </list>
///
/// <para>已知可能选错的场景（如实记录，不假装解决）：</para>
/// <list type="bullet">
///   <item>实验机/服务器上多块网卡处于**同一网段**（vSwitch、多网口做容错绑定）：用户说的"旧电脑"走的可能不是我们按最长前缀挑的那块，
///         此时若能拿到系统选路结论则以系统为准，否则报歧义不可用；</item>
///   <item>IPv6 link-local 不带 scope：多条 <c>fe80::/64</c> 同时存在时系统任挑一条，本类型直接判不可用；</item>
///   <item>目标主机名解析出多个地址，且不同地址走不同网卡（多宿主/双栈）：本类型逐个尝试，取第一个能定位的（见
///         <see cref="Select(IReadOnlyCollection{IPAddress}?, Serilog.ILogger?)"/>），并在理由里写明用的是哪个地址；</item>
///   <item>网卡计数器读到的流量包含该网卡上**一切**流量：若同一网卡还有大量与 PCMig 无关的业务，读数会偏高（这是已知限制）。</item>
/// </list>
///
/// <para>线程安全：无共享可变状态，所有失败路径返回结果而不是抛异常。</para>
/// </summary>
public static class NetworkInterfaceSelector
{
    /// <summary>按单个目标地址选网卡。失败返回 <c>IsUsable == false</c> 的结果（含中文理由），不抛异常。</summary>
    public static InterfaceSelection Select(IPAddress? destination, Serilog.ILogger? log = null)
    {
        if (destination is null)
        {
            log?.Debug("网卡吞吐：未提供目标地址，接口选择不可用");
            return InterfaceSelection.No(InterfaceSelectionOutcome.NoEndpoint, "没有目标地址（主机名未解析出 IPv4/IPv6 地址），无法确定走哪块网卡");
        }

        if (destination.AddressFamily != AddressFamily.InterNetwork &&
            destination.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return InterfaceSelection.No(InterfaceSelectionOutcome.UnsupportedAddressFamily,
                $"不支持的地址族 {destination.AddressFamily}（只支持 IPv4 / IPv6）");
        }

        if (IPAddress.IsLoopback(destination))
        {
            return InterfaceSelection.No(InterfaceSelectionOutcome.Loopback,
                $"目标是本机回环地址 {destination}：本机自我迁移不产生网络流量，真实网卡吞吐不适用");
        }

        var isV6 = destination.AddressFamily == AddressFamily.InterNetworkV6;

        // ① IPv6 link-local 缺 scope：多条 fe80::/64 路由时系统任挑一条 ⇒ 直接判不可用（绝不冒充）。
        if (isV6 && destination.IsIPv6LinkLocal && destination.ScopeId == 0)
        {
            return InterfaceSelection.No(InterfaceSelectionOutcome.LinkLocalWithoutScope,
                $"IPv6 link-local 目标 {destination} 没有 scope id（应形如 fe80::1%接口号）：" +
                "系统会在多条 fe80::/64 路由里任选一条，选出的网卡不可信——请在目标地址后写明 %接口号");
        }

        // ② IPv6 带 scope：Windows 的 sin6_scope_id 就是接口索引，先按它取行信息校验。
        if (isV6 && destination.ScopeId != 0)
        {
            var scoped = unchecked((uint)destination.ScopeId);
            if (IpHelperIfEntry.TryReadRow(scoped, out var scopedRow, out var scopedReason))
            {
                var note = $"依据 IPv6 scope id（{destination} 的 %{scoped}）定位网卡：{DescribeRow(scoped, scopedRow)}";
                log?.Debug("网卡吞吐：{Note}", note);
                return InterfaceSelection.Ok(scoped, scopedRow, "IPv6Scope", note);
            }
            // scope 指到的接口读不到 ⇒ 不硬失败，继续按前缀/选路尝试（不吞掉理由，最后会一并报告）。
            log?.Debug("网卡吞吐：IPv6 scope 指向的接口 {Index} 读取失败：{Reason}", scoped, scopedReason);
        }

        // ③ 同子网地址前缀匹配。
        var match = MatchByAddressPrefix(destination, out var prefixNote);
        if (match.Count == 1)
        {
            var index = match[0].Index;
            var nicName = match[0].NicName;
            var prefix = match[0].PrefixLength;
            if (IpHelperIfEntry.TryReadRow(index, out var row, out var reason))
            {
                var note = $"依据同子网地址前缀（/{prefix}）定位网卡：{DescribeRow(index, row)}；.NET 接口名“{nicName}”；{prefixNote}";
                log?.Debug("网卡吞吐：{Note}", note);
                return InterfaceSelection.Ok(index, row, "AddressPrefix", note);
            }
            log?.Debug("网卡吞吐：前缀匹配到的接口 {Index} 读取失败：{Reason}", index, reason);
        }
        else if (match.Count > 1)
        {
            var names = string.Join("、", match.Select(m => $"#{m.Index}({m.NicName}, /{m.PrefixLength})"));
            log?.Debug("网卡吞吐：同子网并列候选 {Count} 个：{Names}", match.Count, names);
        }

        // ④ 系统选路（Windows 自己的结论）。
        if (IpHelperIfEntry.TryGetBestInterfaceIndex(destination, out var bestIndex, out var bestReason))
        {
            if (IpHelperIfEntry.TryReadRow(bestIndex, out var bestRow, out var bestRowReason))
            {
                var tie = match.Count > 1
                    ? $"注意：同子网并列候选 {match.Count} 块网卡，本次以系统选路结论为准"
                    : prefixNote;
                var note = $"依据系统选路（GetBestInterfaceEx）定位网卡：{DescribeRow(bestIndex, bestRow)}；{tie}";
                log?.Debug("网卡吞吐：{Note}", note);
                return InterfaceSelection.Ok(bestIndex, bestRow, "BestInterface", note);
            }
            return InterfaceSelection.No(InterfaceSelectionOutcome.RowReadFailed,
                $"系统选路给出的接口 {bestIndex} 读不到行信息：{bestRowReason}");
        }

        return match.Count > 1
            ? InterfaceSelection.No(InterfaceSelectionOutcome.Ambiguous,
                $"同一子网有 {match.Count} 块网卡并列最长前缀，系统选路也没定下来（{bestReason}）：" +
                "无从判断旧电脑的流量走哪块网卡，真实网卡吞吐按不可用处理（请改用带 scope 的 IPv6 地址或直接填目标网卡所在网段）")
            : InterfaceSelection.No(InterfaceSelectionOutcome.NoMatchingInterface,
                $"本机没有接口地址与目标 {destination} 同子网，系统选路也失败（{bestReason}）：" +
                "可能还没连上旧电脑所在的网络，或目标不在同一二层网络（这点由现实决定，不做猜测）");
    }

    /// <summary>
    /// 按一组候选地址（例如主机名解析结果，可能同时有 IPv4/IPv6、多条 A 记录）逐个尝试，返回第一个能定位网卡的结果。
    /// 全部失败时返回信息量最大的那个失败结果（跳过"地址族不支持"这类无意义结局）。
    /// </summary>
    public static InterfaceSelection Select(IReadOnlyCollection<IPAddress>? destinations, Serilog.ILogger? log = null)
    {
        if (destinations is null || destinations.Count == 0)
        {
            return InterfaceSelection.No(InterfaceSelectionOutcome.NoEndpoint,
                "没有候选地址（主机名未解析出任何 IPv4/IPv6 地址），无法确定走哪块网卡");
        }

        InterfaceSelection? firstFailure = null;
        foreach (var address in destinations)
        {
            var result = Select(address, log);
            if (result.IsUsable) return result;
            if (result.Outcome == InterfaceSelectionOutcome.UnsupportedAddressFamily) continue;
            firstFailure ??= result;
        }

        if (firstFailure is not null && destinations.Count > 1)
        {
            return firstFailure with { Reason = firstFailure.Reason + $"（已尝试 {destinations.Count} 个候选地址，均无法定位网卡）" };
        }
        return firstFailure ?? InterfaceSelection.No(InterfaceSelectionOutcome.UnsupportedAddressFamily,
            "候选地址全部不是 IPv4/IPv6");
    }

    private readonly record struct PrefixCandidate(uint Index, int PrefixLength, bool IsUp, string NicName);

    /// <summary>
    /// 同子网前缀匹配。返回并列最长前缀的候选集合（按索引去重，保留最长前缀与"是否 Up"）。
    /// 注意：**不做任意挑选**——调用方只在恰好 1 个候选时才直接采信。
    /// </summary>
    private static List<PrefixCandidate> MatchByAddressPrefix(IPAddress destination, out string note)
    {
        note = "";
        var byIndex = new Dictionary<uint, PrefixCandidate>();
        var skipped = 0;

        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            note = $"枚举本机网卡失败（{ex.GetType().Name}）：{ex.Message}";
            return new List<PrefixCandidate>();
        }

        foreach (var nic in nics)
        {
            bool isUp;
            try
            {
                isUp = nic.OperationalStatus == OperationalStatus.Up;
            }
            catch
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                // 某些虚拟/已拔出网卡会在这里抛 NetworkInformationException：直接跳过，不影响其它网卡。
                skipped++;
                continue;
            }

            foreach (var ua in props.UnicastAddresses)
            {
                var addr = ua.Address;
                if (addr.AddressFamily != destination.AddressFamily) continue;
                if (IPAddress.IsLoopback(addr)) continue;
                if (ua.PrefixLength <= 0) continue;
                // IPv6 link-local 一律走 scope 路径：同一 fe80::/64 前缀在所有网卡上都"成立"，前缀匹配在这里没有分辨力。
                if (addr.IsIPv6LinkLocal || destination.IsIPv6LinkLocal) continue;
                if (!SameSubnet(addr.GetAddressBytes(), destination.GetAddressBytes(), ua.PrefixLength)) continue;

                var index = TryGetInterfaceIndex(nic, destination.AddressFamily);
                if (index == 0) continue;

                var candidate = new PrefixCandidate(index, ua.PrefixLength, isUp, nic.Name);
                if (byIndex.TryGetValue(index, out var old))
                {
                    // 同一索引可能被多个地址命中：保留更长前缀；Up 优先。
                    if (candidate.PrefixLength > old.PrefixLength || (candidate.PrefixLength == old.PrefixLength && candidate.IsUp && !old.IsUp))
                        byIndex[index] = candidate;
                }
                else
                {
                    byIndex[index] = candidate;
                }
            }
        }

        if (byIndex.Count == 0)
        {
            note = skipped > 0 ? $"（另有 {skipped} 块网卡属性读取失败，已跳过）" : "";
            return new List<PrefixCandidate>();
        }

        var bestPrefix = byIndex.Values.Max(v => v.PrefixLength);
        var best = byIndex.Values.Where(v => v.PrefixLength == bestPrefix).ToList();

        // 并列最长前缀时优先"已连接"的网卡；若只剩 1 块 Up 的，就可以采信。
        var up = best.Where(v => v.IsUp).ToList();
        if (up.Count == 1) best = up;

        note = skipped > 0 ? $"（另有 {skipped} 块网卡属性读取失败，已跳过）" : "";
        return best;
    }

    /// <summary>取 .NET 侧接口索引（Windows 上 IPv4/IPv6 的 Index 与 IP Helper 的 IF 索引是同一套）。取不到返回 0。</summary>
    private static uint TryGetInterfaceIndex(NetworkInterface nic, AddressFamily family)
    {
        try
        {
            var props = nic.GetIPProperties();
            if (family == AddressFamily.InterNetwork)
            {
                var v4 = props.GetIPv4Properties();
                return v4 is null ? 0 : unchecked((uint)v4.Index);
            }
            var v6 = props.GetIPv6Properties();
            return v6 is null ? 0 : unchecked((uint)v6.Index);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>按位比较两个同族地址是否落在同一网段（避免依赖字符串/正则，也避免 System.Net.IPNetwork 的版本差异）。</summary>
    internal static bool SameSubnet(byte[] a, byte[] b, int prefixLength)
    {
        if (a.Length != b.Length || prefixLength < 0 || prefixLength > a.Length * 8) return false;
        var wholeBytes = prefixLength / 8;
        var restBits = prefixLength % 8;
        for (var i = 0; i < wholeBytes; i++)
        {
            if (a[i] != b[i]) return false;
        }
        if (restBits == 0) return true;
        var mask = (byte)(0xFF << (8 - restBits));
        return (a[wholeBytes] & mask) == (b[wholeBytes] & mask);
    }

    private static string DescribeRow(uint index, in InterfaceRowInfo row)
    {
        var link = row.ReceiveLinkSpeed > 0
            ? $"{row.ReceiveLinkSpeed / 1_000_000} Mbps"
            : "链路速率未知";
        return $"接口 #{index}（{row.Alias} / {row.Description}，{link}，OperStatus={row.OperStatus}，MediaConnectState={row.MediaConnectState}）";
    }
}
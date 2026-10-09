using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PCMig.Core.Native;

/// <summary>
/// 网卡行信息（MIB_IF_ROW2 里我们真正要用的那几个字段）。只读快照，纯数据。
/// </summary>
/// <param name="InterfaceIndex">系统接口索引（IPv4/IPv6 共用同一套 IF 索引）。</param>
/// <param name="Description">网卡描述（如 "Intel(R) Ethernet Connection I219-V"），用于如实报告"读的是哪块网卡"。</param>
/// <param name="Alias">网卡别名（用户在"网络连接"里看到的名字，如 "以太网"）。</param>
/// <param name="OperStatus">IF_OPER_STATUS（1 = Up）。</param>
/// <param name="MediaConnectState">NET_IF_MEDIA_CONNECT_STATE（1 = Connected）。</param>
/// <param name="Type">IFTYPE（6 = IF_TYPE_ETHERNET_CSMACD，71 = IEEE80211 无线）。</param>
/// <param name="ReceiveLinkSpeed">协商接收链路速率（bit/s；无线/虚拟网卡可能为 0）。</param>
/// <param name="InOctets">累计接收字节（网卡计数器，单调递增，除非驱动重置/回绕）。</param>
/// <param name="OutOctets">累计发送字节（本任务不需要，保留以便诊断对照）。</param>
public readonly record struct InterfaceRowInfo(
    uint InterfaceIndex,
    string Description,
    string Alias,
    uint OperStatus,
    uint MediaConnectState,
    uint Type,
    ulong ReceiveLinkSpeed,
    ulong InOctets,
    ulong OutOctets)
{
    /// <summary>接口是否在"已连接"状态（OperStatus=Up 且介质已连接）。</summary>
    public bool IsOperational => OperStatus == 1 && MediaConnectState == 1;
}

/// <summary>
/// Windows IP Helper（iphlpapi.dll）最小封装：<c>GetIfEntry2</c> 读网卡累计接收字节（<c>MIB_IF_ROW2.InOctets</c>），
/// 以及 <c>GetBestInterfaceEx</c> 由目标地址反查"访问它要经过哪块本机网卡"。
///
/// <para>
/// 【为什么需要它】界面上的速率过去来自 <c>ProgressTruthSnapshot.SpeedBytesPerSecond</c>，口径是
/// "本轮新增**逻辑完成**字节 ÷ 本轮有效运行时间"。这条口径有两个确定的偏差：
/// <list type="number">
///   <item>目标端**已存在**的文件被 Robocopy 快速 Skip 时，逻辑字节照样飞速进入完成口径
///         （35 GB 已存在、Robocopy 只检查约 0.2 秒 ⇒ 逻辑上"完成"35 GB），
///         但这 35 GB **根本没有再次经过网卡**；</item>
///   <item>SMB / 文件缓存会让 Preflight 测速虚高（32 MB 测试文件 2 秒内累计读数 GB ⇒ 约 4 GB/s，
///         这不是物理网络吞吐）。</item>
/// </list>
/// 所以"逻辑完成速率"与"真实网络吞吐"必须分开：本类型只提供**真实网卡计数器**，
/// 供界面实时速度与 ETA 使用，**绝不**参与 Receipt / Progress Truth / CompletedBytes / Verifier。
/// </para>
///
/// <para>
/// 【布局正确性怎么保证（本任务最大的技术风险）】字段错位会读到垃圾值，因此这里用三重防线：
/// <list type="number">
///   <item>结构体字段顺序/类型严格照 ifmib.h 的 <c>MIB_IF_ROW2</c> 声明书写，含 257 个 WCHAR 的
///         <c>Alias</c>/<c>Description</c>（用 <c>ushort[]</c> + <c>ByValArray/ArraySubType=U2</c> 表达，
///         不用 <c>char[]</c>——<c>char[]</c> 的元素宽度会随 <c>CharSet</c> 变化，是最容易埋雷的地方）；</item>
///   <item><see cref="LayoutSelfCheckPassed"/> 用 <c>Marshal.OffsetOf</c> 把
///         <c>InterfaceIndex</c>/<c>PhysicalAddressLength</c>/<c>InOctets</c> 的实际偏移与文档值
///         （8 / 1056 / 1208）逐一比对，并核对 <c>Marshal.SizeOf == 1352</c>；
///         **自检不通过就一律返回"不可用"，绝不拿错位的内存冒充读数**；</item>
///   <item>调用 <c>GetIfEntry2</c> 时用固定 2048 字节的临时缓冲（&gt; 1352），
///         OS 最多写 1352 字节，因此即使我方结构体声明有问题也**不会越界写坏托管内存**；
///         读回后校验 <c>row.InterfaceIndex</c> 是否等于请求的索引（不等即视为布局不可信）。</item>
/// </list>
/// </para>
///
/// <para>线程安全：本类型无共享可变状态（每次调用各自申请/释放缓冲），可并发调用；
/// 所有 P/Invoke 失败路径都**返回 false + 中文原因**，绝不抛异常（后台采样线程上抛异常会直接杀进程）。</para>
/// </summary>
public static class IpHelperIfEntry
{
    // ---- 文档偏移常量（来自 ifmib.h 的 MIB_IF_ROW2 声明，x64 自然对齐/默认 pack=8；32 位同值：无 8 字节前置字段）----
    /// <summary>MIB_IF_ROW2 的总长度（文档值）。</summary>
    public const int DocumentedRowSizeBytes = 1352;
    /// <summary>InterfaceIndex 偏移：InterfaceLuid(8) 之后。</summary>
    public const int OffsetOfInterfaceIndex = 8;
    /// <summary>PhysicalAddressLength 偏移：8+4+16(GUID)+257*2(Alias)+257*2(Description) = 1056。</summary>
    public const int OffsetOfPhysicalAddressLength = 1056;
    /// <summary>InOctets 偏移：1056 +4(长度) +32 +32(物理地址) +4(Mtu) +4+4+4+4+4+4(六个枚举) +1(位标志) +3(填充)
    /// +4+4+4(OperStatus/AdminStatus/MediaConnectState) +16(NetworkGuid) +4(ConnectionType) +4(补齐 8 字节对齐)
    /// +8(TransmitLinkSpeed) +8(ReceiveLinkSpeed) = 1208。</summary>
    public const int OffsetOfInOctets = 1208;

    /// <summary>临时缓冲大小：必须 ≥ <see cref="DocumentedRowSizeBytes"/>，实际给足余量防止 OS 越界写。</summary>
    private const int ScratchBufferBytes = 2048;

    private const int ErrorSuccess = 0;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorNotFound = 1168;
    private const int ErrorNetworkUnreachable = 1231;
    private const int ErrorHostUnreachable = 1232;

    private const ushort AfInet = 2;    // Windows 的 AF_INET
    private const ushort AfInet6 = 23;  // Windows 的 AF_INET6

    /// <summary>
    /// ★ ifmib.h: MIB_IF_ROW2 ★ 顺序/类型必须与头文件完全一致。
    /// 只读 InOctets/Description/OperStatus 等字段，但**字段必须列全**（至少到 InOctets 之后），
    /// 因为 OS 会把整行 1352 字节写进我们给的缓冲；声明不全也不会越界（缓冲给了 2048），
    /// 但读回结构体时需要的字段必须在位。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;          // 0
        public uint InterfaceIndex;          // 8
        public Guid InterfaceGuid;           // 12
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 257, ArraySubType = UnmanagedType.U2)]
        public ushort[] Alias;               // 28   （WCHAR[IF_MAX_STRING_SIZE+1]，IF_MAX_STRING_SIZE=256）
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 257, ArraySubType = UnmanagedType.U2)]
        public ushort[] Description;         // 542
        public uint PhysicalAddressLength;   // 1056
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.U1)]
        public byte[] PhysicalAddress;       // 1060 （IF_MAX_PHYS_ADDRESS_LENGTH = 32）
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.U1)]
        public byte[] PermanentPhysicalAddress; // 1092
        public uint Mtu;                     // 1124
        public uint Type;                    // 1128  IFTYPE
        public uint TunnelType;              // 1132  TUNNEL_TYPE
        public uint MediaType;               // 1136  NDIS_MEDIUM
        public uint PhysicalMediumType;      // 1140  NDIS_PHYSICAL_MEDIUM
        public uint AccessType;              // 1144  NET_IF_ACCESS_TYPE
        public uint DirectionType;           // 1148  NET_IF_DIRECTION_TYPE
        public byte InterfaceAndOperStatusFlags; // 1152  8 个 BOOLEAN:1 位标志，共 1 字节
        public uint OperStatus;              // 1156  （1153 起 3 字节对齐填充）IF_OPER_STATUS
        public uint AdminStatus;             // 1160  NET_IF_ADMIN_STATUS
        public uint MediaConnectState;       // 1164  NET_IF_MEDIA_CONNECT_STATE
        public Guid NetworkGuid;             // 1168  NET_IF_NETWORK_GUID
        public uint ConnectionType;          // 1184  NET_IF_CONNECTION_TYPE
        public ulong TransmitLinkSpeed;      // 1192  （1188 起 4 字节对齐填充）
        public ulong ReceiveLinkSpeed;       // 1200
        public ulong InOctets;               // 1208  ★ 本任务唯一核心读数 ★
        public ulong OutOctets;              // 1216
        public ulong InUcastPkts;            // 1224
        public ulong InNUcastPkts;           // 1232
        public ulong InDiscards;             // 1240
        public ulong InErrors;               // 1248
        public ulong InUnknownProtos;        // 1256
        public ulong InUcastOctets;          // 1264
        public ulong InMulticastOctets;      // 1272
        public ulong InBroadcastOctets;      // 1280
        public ulong OutUcastPkts;           // 1288
        public ulong OutNUcastPkts;          // 1296
        public ulong OutDiscards;            // 1304
        public ulong OutErrors;              // 1312
        public ulong OutUcastOctets;         // 1320
        public ulong OutMulticastOctets;     // 1328
        public ulong OutBroadcastOctets;     // 1336
        public ulong OutQLen;                // 1344  → 结构体总长 1352
    }

    /// <summary>SOCKADDR_IN（16 字节）。sin_zero 用 ulong 表达（8 字节，恒为 0）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SOCKADDR_IN
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;    // 网络字节序：内存里是 b0 b1 b2 b3
        public ulong sin_zero;   // char sin_zero[8]
    }

    /// <summary>SOCKADDR_IN6（28 字节）。IPv6 的 scope 必须走 sin6_scope_id，否则 link-local 无法定位接口。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SOCKADDR_IN6
    {
        public ushort sin6_family;
        public ushort sin6_port;
        public uint sin6_flowinfo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16, ArraySubType = UnmanagedType.U1)]
        public byte[] sin6_addr;
        public uint sin6_scope_id;
    }

    // GetIfEntry2 取 PMIB_IF_ROW2；这里收 IntPtr，由调用方保证缓冲 ≥ 1352 且已清零。
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(IntPtr row);

    [DllImport("iphlpapi.dll", EntryPoint = "GetBestInterfaceEx", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(ref SOCKADDR_IN destAddr, out uint bestIfIndex);

    [DllImport("iphlpapi.dll", EntryPoint = "GetBestInterfaceEx", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(ref SOCKADDR_IN6 destAddr, out uint bestIfIndex);

    // ---- 布局自检（懒执行一次；失败 ⇒ 全部读取路径返回"不可用"，绝不读垃圾）----
    private static readonly Lazy<(bool Ok, string Detail)> s_layout =
        new(VerifyLayout, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>MIB_IF_ROW2 偏移/长度自检是否通过。不通过时任何读数都被判为不可信。</summary>
    public static bool LayoutSelfCheckPassed => s_layout.Value.Ok;

    /// <summary>自检细节（通过时给出各偏移，不通过时给出差异），用于日志与界面如实报告。</summary>
    public static string LayoutSelfCheckDetail => s_layout.Value.Detail;

    /// <summary>本机是否缺少 iphlpapi（非 Windows）或被系统拒绝加载。</summary>
    public static bool PlatformUnavailable { get; private set; }

    private static (bool Ok, string Detail) VerifyLayout()
    {
        try
        {
            var size = Marshal.SizeOf<MIB_IF_ROW2>();
            var idx = Marshal.OffsetOf<MIB_IF_ROW2>(nameof(MIB_IF_ROW2.InterfaceIndex)).ToInt64();
            var pal = Marshal.OffsetOf<MIB_IF_ROW2>(nameof(MIB_IF_ROW2.PhysicalAddressLength)).ToInt64();
            var ino = Marshal.OffsetOf<MIB_IF_ROW2>(nameof(MIB_IF_ROW2.InOctets)).ToInt64();

            var ok = size == DocumentedRowSizeBytes
                     && idx == OffsetOfInterfaceIndex
                     && pal == OffsetOfPhysicalAddressLength
                     && ino == OffsetOfInOctets;

            var detail = ok
                ? $"MIB_IF_ROW2 布局自检通过（size={size}, InterfaceIndex={idx}, PhysicalAddressLength={pal}, InOctets={ino}）"
                : $"MIB_IF_ROW2 布局自检未通过：实测 size={size}/InterfaceIndex={idx}/PhysicalAddressLength={pal}/InOctets={ino}，" +
                  $"文档值 {DocumentedRowSizeBytes}/{OffsetOfInterfaceIndex}/{OffsetOfPhysicalAddressLength}/{OffsetOfInOctets}——" +
                  "为免读到错位内存，网卡吞吐观测一律按“不可用”处理（不改用猜测值）";
            return (ok, detail);
        }
        catch (Exception ex)
        {
            return (false, "MIB_IF_ROW2 布局自检异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// 读取指定接口的完整行信息。失败（接口不存在 / P/Invoke 失败 / 布局不可信 / 非 Windows）返回 false + 中文原因，<b>不抛异常</b>。
    /// </summary>
    public static bool TryReadRow(uint interfaceIndex, out InterfaceRowInfo info, out string reason)
    {
        info = default;
        if (interfaceIndex == 0)
        {
            reason = "接口索引为 0：调用方尚未确定真实网卡（不得随便挑一块网卡冒充）";
            return false;
        }
        if (!LayoutSelfCheckPassed)
        {
            reason = LayoutSelfCheckDetail;
            return false;
        }

        var buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(ScratchBufferBytes);
            // 全零：GetIfEntry2 只认 InterfaceLuid / InterfaceIndex，其余字段必须为 0。
            for (var off = 0; off < ScratchBufferBytes; off += sizeof(long))
                Marshal.WriteInt64(buffer, off, 0L);
            Marshal.WriteInt32(buffer, OffsetOfInterfaceIndex, unchecked((int)interfaceIndex));

            var ret = GetIfEntry2(buffer);
            if (ret != ErrorSuccess)
            {
                reason = DescribeGetIfEntry2Error(ret, interfaceIndex);
                return false;
            }

            var row = Marshal.PtrToStructure<MIB_IF_ROW2>(buffer);
            if (row.InterfaceIndex != interfaceIndex)
            {
                // 读回的索引与请求不符 ⇒ 布局不可信（宁可报不可用，也不展示可能错位的数字）。
                reason = $"读回的 InterfaceIndex={row.InterfaceIndex} 与请求的 {interfaceIndex} 不一致：MIB_IF_ROW2 布局不可信，本次读数作废";
                return false;
            }
            if (row.InOctets == 0 && row.OutOctets == 0 && row.Description is null)
            {
                reason = $"接口 {interfaceIndex} 的行信息为空（可能是已拔出的虚拟网卡）";
                return false;
            }

            info = new InterfaceRowInfo(
                row.InterfaceIndex,
                FixedWideToString(row.Description),
                FixedWideToString(row.Alias),
                row.OperStatus,
                row.MediaConnectState,
                row.Type,
                row.ReceiveLinkSpeed,
                row.InOctets,
                row.OutOctets);
            reason = "";
            return true;
        }
        catch (DllNotFoundException)
        {
            PlatformUnavailable = true;
            reason = "本机没有 iphlpapi.dll（非 Windows 平台）：网卡吞吐观测不可用";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            PlatformUnavailable = true;
            reason = "iphlpapi.dll 中缺少 GetIfEntry2（系统过旧，需 Windows Vista 及以上）：网卡吞吐观测不可用";
            return false;
        }
        catch (BadImageFormatException)
        {
            PlatformUnavailable = true;
            reason = "iphlpapi.dll 加载失败（位数不匹配）：网卡吞吐观测不可用";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"读取网卡行信息异常（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 读取指定接口的累计接收字节（<c>InOctets</c>）。失败返回 false + 中文原因，不抛异常。
    /// </summary>
    public static bool TryReadInOctets(uint interfaceIndex, out ulong inOctets, out string reason)
    {
        inOctets = 0;
        if (!TryReadRow(interfaceIndex, out var info, out reason)) return false;
        inOctets = info.InOctets;
        return true;
    }

    /// <summary>
    /// 由**目标地址**反查"访问它要经过本机哪块网卡"（<c>GetBestInterfaceEx</c>，即系统自己的选路结果）。
    ///
    /// <para>IPv4 与 IPv6 是两种不同的 sockaddr 形式（本方法内部各走一条）；IPv6 的 scope 必须写进
    /// <c>sin6_scope_id</c>，否则 link-local（fe80::/10）无法定位接口。</para>
    ///
    /// <para>已知限制：link-local 地址若 <c>ScopeId == 0</c>，系统只会在多条 fe80::/64 路由里"挑一条"，
    /// 结果是任意的——那种情况由上层（<see cref="NetworkInterfaceSelector"/>）直接判为不可用，不走本方法。</para>
    /// </summary>
    public static bool TryGetBestInterfaceIndex(IPAddress destination, out uint interfaceIndex, out string reason)
    {
        interfaceIndex = 0;
        if (destination is null)
        {
            reason = "目标地址为空";
            return false;
        }
        if (!LayoutSelfCheckPassed)
        {
            reason = "跳过选路：先决条件不满足（" + LayoutSelfCheckDetail + "），GetBestInterfaceEx 的结果同样不可信";
            return false;
        }

        try
        {
            uint ret;
            if (destination.AddressFamily == AddressFamily.InterNetwork)
            {
                var sa = new SOCKADDR_IN
                {
                    sin_family = AfInet,
                    sin_port = 0,
                    sin_addr = IPv4ToNetworkUint(destination),
                    sin_zero = 0,
                };
                ret = GetBestInterfaceEx(ref sa, out interfaceIndex);
            }
            else if (destination.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var sa6 = new SOCKADDR_IN6
                {
                    sin6_family = AfInet6,
                    sin6_port = 0,
                    sin6_flowinfo = 0,
                    sin6_addr = destination.GetAddressBytes(),     // 恰好 16 字节
                    sin6_scope_id = unchecked((uint)destination.ScopeId),
                };
                ret = GetBestInterfaceEx(ref sa6, out interfaceIndex);
            }
            else
            {
                reason = $"不支持的地址族 {destination.AddressFamily}（只支持 IPv4/IPv6）";
                return false;
            }

            if (ret != ErrorSuccess)
            {
                interfaceIndex = 0;
                reason = DescribeBestInterfaceError(ret, destination);
                return false;
            }
            if (interfaceIndex == 0)
            {
                reason = $"系统对 {destination} 未给出接口索引（0）";
                return false;
            }
            reason = "";
            return true;
        }
        catch (DllNotFoundException)
        {
            PlatformUnavailable = true;
            reason = "本机没有 iphlpapi.dll（非 Windows 平台）：无法由目标地址反查网卡";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            PlatformUnavailable = true;
            reason = "iphlpapi.dll 中缺少 GetBestInterfaceEx（系统过旧）：无法由目标地址反查网卡";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"由目标地址反查网卡异常（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }
    }

    /// <summary>sin_addr 是网络字节序：内存里按 b0 b1 b2 b3 排列，故 uint 值 = b0 | b1&lt;&lt;8 | b2&lt;&lt;16 | b3&lt;&lt;24。</summary>
    private static uint IPv4ToNetworkUint(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4) return 0;
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    /// <summary>固定长度 WCHAR 缓冲 → string（在首个 '\0' 处截断）。</summary>
    private static string FixedWideToString(ushort[]? buffer)
    {
        if (buffer is null || buffer.Length == 0) return "";
        var end = Array.IndexOf(buffer, (ushort)0);
        if (end < 0) end = buffer.Length;
        if (end == 0) return "";
        // ★ 2026-10-08 修复 ★ WCHAR 缓冲是 ushort[]，.NET 没有 string(ushort[],int,int) 重载
        //   （只有 char[] 版本），必须逐元素转 char。原写法在本机编译报 CS1503。
        var chars = new char[end];
        for (var i = 0; i < end; i++) chars[i] = (char)buffer[i];
        return new string(chars).TrimEnd('\0');
    }

    private static string DescribeGetIfEntry2Error(uint code, uint interfaceIndex) => code switch
    {
        ErrorNotFound => $"接口索引 {interfaceIndex} 在本机不存在（网卡已被拔出/禁用，或索引来自别的机器）",
        ErrorInvalidParameter => $"GetIfEntry2 认为参数非法（索引 {interfaceIndex}；通常是索引为 0 或系统过旧）",
        ErrorNotSupported => $"系统不支持 GetIfEntry2（Windows Vista 以下）：索引 {interfaceIndex}",
        _ => $"GetIfEntry2 读接口 {interfaceIndex} 失败：Win32Error={code}（{new System.ComponentModel.Win32Exception((int)code).Message}）",
    };

    private static string DescribeBestInterfaceError(uint code, IPAddress destination) => code switch
    {
        ErrorNetworkUnreachable => $"本机没有到 {destination} 的路由（网络不可达）：检查是不是还没连上旧电脑所在的网络",
        ErrorHostUnreachable => $"到 {destination} 的主机不可达：目标可能已关机或不在同网段",
        ErrorInvalidParameter => $"GetBestInterfaceEx 认为目标地址非法（{destination}）；IPv6 link-local 需要 %scope 才能定位接口",
        ErrorNotSupported => $"系统不支持该地址族的 GetBestInterfaceEx（Windows Vista 以下）：{destination}",
        _ => $"GetBestInterfaceEx 对 {destination} 失败：Win32Error={code}（{new System.ComponentModel.Win32Exception((int)code).Message}）",
    };
}
# 技术发现：Whole-App Uniform Scaling（UniformScaleHost Spike）验证通过

- 日期：2026-09-28
- 范围：**纯视觉层**（Presentation + 一处 Window 接线），未触碰 ViewModel / Command / 绑定 / 迁移与 Robocopy 逻辑 / 状态机 / 错误处理 / 网络检测 / 存档格式
- 默认关闭：未设 `PCMIG_UNIFORM_HOST=1` 时生产路径**逐位不变**（XAML 一字未改）

## 结论

`Viewbox(Stretch=Uniform)` + 固定 1424×892 DesignSurface + 运行时重挂，是 PCMig 实现"整台应用等比缩放"的可行方案。

- 几何抽样 **174/174 PASS**，最大绝对误差 **0.88 px**
- 四页 × 四档（1.00/0.95/0.90/0.85）目视均为"完整的小号 PCMig"，**无重叠、无裁切、无结构 reflow、无页级滚动条**
- Native Caption Buttons 不参与缩放（46×32 恒定）
- 实测交互：点击/输入/滚轮/标题栏拖动/浮层定位全部正常

完整证据与脚本索引见 `archive\screenshots\uniform-host\SPIKE-RESULT.md`。

## 为什么不能用"XAML 里套 Viewbox + 默认 Stretch=None"

`Viewbox` 在 `Stretch=None` 下自身尺寸 = 子元素 DesiredSize。子元素固定 1424×892 ⇒ 窗口无论多大内容都固定 1424×892（生产路径崩坏）；`Width/Height=NaN` 时又按自然尺寸量取，内容不再填满窗口。
⇒ 靠改 Viewbox 属性**无法**做到"默认零变化"，必须用运行时重挂。

## 实现（`src\PCMig.WinUI\Presentation\UniformScaleHost.cs`）

构造函数**最末**一步执行：摘除 `window.Content` → 放入固定 1424×892 的 DesignSurface → DesignSurface 作为 `Viewbox.Child`（`Stretch=Uniform`、`StretchDirection=Both`）→ Viewbox 挂回 `window.Content`。任一步失败自动把旧根还回窗口，不留下空窗口。

## 必须记住的四个集成点（下次改这块先看这里）

1. **`FindName` 名字作用域**：重挂后 `window.Content` 是 Viewbox，而全部 `x:Name` 仍在旧根作用域。任何"从 `window.Content` 向下 FindName"的代码会**静默失效**（不报错）。已引入 `MainWindow.ApplicationRoot` 并在响应式与浮层定位两处使用。
2. **浮层定位坐标系**：`PositionOverlayPanels` 的 root 尺寸必须取 DesignSurface 的固定尺寸（1424×892），否则会用真实窗口尺寸去夹紧未缩放的本地坐标。
3. **自定义标题栏拖动区**：`SetTitleBar` 的元素被一起缩放，实测拖动区仍然有效（设计 x=60 处拖动 → 窗口位移 37,23）。**不要**为此把标题栏排除在缩放外。
4. **视口自校正观测口径**：Uniform 模式下原根尺寸恒等于设计面，其 `SizeChanged` 不再代表视口 ⇒ 必须改读 `XamlRoot.Size`。否则启动时那次一次性校正会提前返回，窗口高度多出约 30 DIP 空白带（修正前实测 client=1424×922，修正后 1424×892）。

## 纪律（写给后续改动）

- **严禁非等比**：只用 `Stretch.Uniform`；不用 `UniformToFill`、不拉伸、不裁 UI
- **Uniform 模式下禁止结构 reflow**：`ApplyResponsiveLayout` 锁定 Canonical 尺寸，Step2 左右结构 / Step3 排列 / Step4 工具栏一律不受窗口尺寸影响
- **不做单点 Font Clamp**：若将来字号不可接受，正确做法是提高整体 `MinimumUIScale` 并禁止窗口继续缩小
- **DPI**：`ApplicationUIScale` 全在 DIP 体系内，**绝不**再乘 `RasterizationScale`

## 证据局限（如实记录）

`PrintWindow(hwnd,hdc,2)` 的重绘路径**不包含实时指针状态**：悬停/按住侧栏卡时像素差恒为 0.00。已在**生产路径**上复现同样结果 ⇒ 属捕获路径限制。指针命中链路由 3 次真实点击命中正确目标覆盖。详见 SPIKE-RESULT 第 6 节。
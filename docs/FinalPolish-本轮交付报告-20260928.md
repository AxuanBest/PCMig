# Final Polish 本轮交付报告（20260928）

> 依据：`FinalPolish-用户指令原文-20260927.md`（67 节，SHA256 已核对归档）
> 配套：`工作交接-20260928-FinalPolish收尾-Motion修复与裁切证伪.md`、`FinalPolish-完成度审计-20260928.md`
> 纪律声明：本报告遵守死律 1（功能冻结）、7（看不见的东西不改）、8（脚本 BOM）、9（无破坏性 git）、
> 10（交接只增不覆）、11（子模型执行 + 主控核验）。**§64：不以 Build 通过 / Tests 通过 / 截图数量 / 「动画代码写了」充当完成。**

---

## 一、修改文件清单

| 文件 | 性质 | 说明 |
|---|---|---|
| `src\PCMig.WinUI\Presentation\MotionDirector.cs` | **改写** | Motion 统一消费层。位移改走 `EntranceThemeTransition`；透明度走 Storyboard；异常一律吸附终态 |
| `src\PCMig.WinUI\MainWindow.xaml.cs` | 改 | `ApplyResponsiveLayout` 接 `ShellResponsiveLayout.Apply`；导航 Changing/Changed 改为方向性入场 + 不再叠 Storyboard |
| `src\PCMig.WinUI\Presentation\StepNavigation.cs` | 改（前序） | 新增 `Changed` 事件 |
| `src\PCMig.WinUI\Presentation\ResponsiveLayoutController.cs` | 改（子代理重写 + 本轮修） | 31+38 Token；本轮修 `FooterSpacerWidth` 限定名、新增 `StepSubtitleMaxWidth` |
| `src\PCMig.WinUI\Presentation\ShellResponsiveLayout.cs` | 新增（子代理） | Token 落到 Header/底栏/外壳/Step1；本轮加 `StepNav.MaxWidth` |
| `src\PCMig.WinUI\Views\StepNavigationControl.xaml` | 改 | 副标题 `TextTrimming="CharacterEllipsis"`；悬停调查注释 |
| `src\PCMig.WinUI\Views\Step{1..4}*.xaml` | 改（前序） | 移除与 Shell Storyboard 重复的声明式转场；Step2 两处补 `TextWrapping` |
| `src\PCMig.WinUI\MainWindow.xaml`、`Views\Step1ConnectPage.xaml` | 改（子代理） | **只加 x:Name**（30/25 个），未改任何布局数值 |
| `archive\scripts\winui-motion-burst.ps1` | 新增 | 动效爆发抓帧（**本轮补上缺失的 UTF-8 BOM**） |
| `archive\scripts\winui-shoot-all.ps1` | 新增 | 四页 UIA 探针 + 抓图 |

**明确未改动**：`tools\release.ps1`、`tools\uishot.ps1`、`tools\stability-test.ps1`、
一切业务逻辑（Robocopy 引擎 / 迁移策略 / 权限 / 连接 / 共享枚举 / 日志解析 / 错误判定 / 校验 / 报告 / 安全策略 / ViewModel）、
`job-state.json` / `plan.json` / Receipt 存档格式、四步流程与导航顺序。
**用户标注图未删未改**：只读副本在 `archive\annotations\new-folder-5\`，与桌面原图 SHA256 一致。

---

## 二、本会话的核心工作

### 1. 修掉一个我自己引入的致命回归：四页永久隐形
- **现象**：主内容区全空，只剩背景与右下两个按钮。
- **根因**（运行期实测捕获，异常原先被吞）：
  `COMException: Cannot resolve TargetProperty (UIElement.Translation.X)`
  —— `Translation` 是 Vector3，不是可动画 DIP；裸 `TranslateX` 路径也解析不了（属 RenderTransform）。
  异常被 `catch` 吞掉后只剩一个停在 0 的 `Opacity`，于是"动画失败"伪装成"页面本来就空"。
- **修法**：**位移交给声明式转场、透明度交给 Storyboard**，各走能解析的那条路径。
- **验证**：四页恢复完整渲染（`final\final-step{1..4}.png`）。

### 2. 让响应式真正生效 + 修好一个真实硬裁切
- 接上 `ShellResponsiveLayout.Apply`（子代理成果原为零调用点的惰性代码），并修掉其 3 处编译错误。
- **修好**：Compact 档侧栏副标题被硬裁到**半个汉字**（步骤 3 缺「错」、步骤 4 缺「常清单」）。
  修法 = `TextTrimming="CharacterEllipsis"` + 新 Token `StepSubtitleMaxWidth`。
  实测结果：`f-compact-1000-after.png` / `final-compact.png` 显示 `实时进度、文件流与…` 等**省略号**。
- **窗口矩阵新补**：16:9(1440×810)、3:2(1350×900)、Compact(1000×700 / 980×700)、Canonical(1440×900) 全测。

### 3. 把两条"待修裁切"证伪（避免下一轮误改）
用**改变窗口高度**做判别：Step2 安全提示卡与 Step4 实时日志在 1440×1100 下**末行完整显示**，
且下边缘仍有余量 ⇒ **是滚动视口底边裁剪，不是文字缺陷**。
**⚠️ 更正（20260927 复核）**：本节原先称"Dev Panel「导出 JSC」是误报、源码为 `导出 JSON`"——
**该更正结论本身是错的，标注②实际未修复**。详见下方"更正说明"。
Update Log「只列 13 版」这一条仍成立：该面板本就设计为可滚动。

### 3.1 更正说明：Dev Panel 按钮溢出（标注② 未修复）

**原判定（错）**：文案是 `导出 JSON`、源码无误 ⇒ 判为误报。
**实际（对）**：**文案对，但按钮被面板右内缘竖直裁切**，所以肉眼读成「导出 JSC」。

三条独立证据：

1. **源码算术**：`Views\DeveloperTuningPanel.xaml:11` `Width="348"` + `:19` `Padding="16"`
   ⇒ 内容宽仅 **316 DIP**；`:78` 是 `StackPanel Orientation="Horizontal" Spacing="8"`（**不换行**）；
   四个按钮（`:79/:82/:85/:88`，各 `Padding="10,6"`）——「恢复默认/复制参数/保存/导出 JSON」
   合计约需 **374 DIP**，**溢出约 58 DIP**，且横向 StackPanel 不会自动换行 ⇒ 必然裁切。
2. **像素实测**（`p-updatelog.png`，该文件名与内容互换，本图实为"开发者材质调节"面板）：
   - 面板左缘 1042 + 348 = **内缘右缘 x = 1390**（精确吻合）
   - 按钮蓝底在 **x=1389 `RGB(19,106,234)` → x=1390 `RGB(251,248,249)` 竖直硬切**，
     **无圆角、无右边框** ⇒ 被父容器裁掉
   - 文字墨迹止于 ≈x 1360，而按钮底延伸到 1390 ⇒ **末段 30px 只有底色、没有字**
3. **视觉模型**：同一截图独立读出「导出 JSC」，与残字现象一致。

⇒ **标注②"显示不全"= 真实缺陷，未修复。** 修法建议：把该 `StackPanel` 换成带 `Wrap` 的
`ItemsWrapGrid`/`WrapPanel`，或缩短文案/减小 `Padding`，或把面板 `Width` 提到 ≥ 410。
**未实施**（属 §3.5 需当轮授权的改动）。

**同理更正**：`工作交接-20260928-FinalPolish收尾-Motion修复与裁切证伪.md:109-110` 的这一条结论同样作废。

**截图文件名与内容互换**（需记住，避免再次误引）：
`p-devpanel.png` 实为「更新日志」面板；`p-updatelog.png` 实为「开发者材质调节」面板。

---

## 三、逐项：人工标注问题（已修 / 未修 / 原因）

| # | 问题 | 状态 | 依据 |
|---|---|---|---|
| 1 | 窗口 Resize 显示不全 | **已修** | 响应式 Token 接线 + 矩阵实测 |
| 2 | 最小窗口尺寸 | 前序已做 | `MinimumClientWidthDip=960` / `MinimumClientHeightDip=640` + `WM_GETMINMAXINFO` |
| 3 | 页面标题数字 Badge 裁切/偏位 | **视觉已达标** | 四页数字 1/2/3/4 **完整居中**（截图确认）；但**共享 Style 未合并**（见未修 1） |
| 4 | Input Placeholder 不居中 | 前序已修（本会话目视复核通过） | 三个输入框占位符垂直居中 |
| 5 | 左侧 Step Badge 与 Icon 过近 | 前序已修（本会话目视复核通过） | 数字圈与图标已明显分开，无粘连 |
| 6 | Step2 安全提示文字裁切 | **证伪，未改** | 滚动视口裁剪；加高窗口后完整 |
| 7 | Step4 实时日志行裁切 | **证伪，未改** | 同上；该面板为静态 4 行、由页面滚动承接 |
| 8 | 侧栏悬停零反馈 | **未修（已回退）** | 指针移动事件未到达卡片，根因未定位（详见交接第四节） |
| 9 | Developer Panel 入口位置 | 前序已做 | 已移至标题栏身份区（本人目视确认在 v0.5.0 徽章旁） |
| 10 | Update Log 恢复 | 前序已做 | 面板可打开，49 版本、左列表右详情、可滚动 |
| 11 | Badge 共享 Style（§12） | **未修** | 见未修 1 |
| 12 | Badge 材质质感（§13） | **未修** | 见未修 1 |

---

## 四、Build / Tests / DPI / 窗口尺寸

| 项目 | 结果 |
|---|---|
| WinUI Release build | **0 警告 0 错误** |
| 全解决方案 Release build | **0 警告 0 错误** |
| Tests | **130/130 通过**，0 失败 0 跳过（与基线一致，**未删未跳未弱化**，§48 满足） |
| `git diff --check -- src/PCMig.WinUI` | exit 0，无空白错误 |
| **DPI 125% / 150%** | **未测 —— 阻塞**：需改系统缩放，属系统级改动，**未获当轮授权** |
| 窗口尺寸 | 16:9 / 3:2 / Compact×2 / Canonical **全部实测**（原先只测了 16:10） |

---

## 五、证据清单（`archive\screenshots\final-polish-20260928\`）

| 用途 | 文件 |
|---|---|
| **四页最终截图** | `final\final-step{1..4}.png` |
| Compact 侧栏省略号复验 | `final\final-compact.png` |
| 窗口矩阵 | `responsive\a-16x9-…` / `b-3x2-…` / `c-compact-…` / `d-compact-…` / `e-canonical-…` |
| 侧栏裁切修复前后 | `responsive\f-compact-1000-after.png`（对比 `responsive\c-compact-1000-step1.png`） |
| **Developer Panel 截图** | `p-devpanel.png` |
| **Update Log 截图** | `p-updatelog.png` |
| 裁切证伪（加高窗口） | `verify\v2-step2-tall2.png`、`responsive\g-step4-1240.png` |
| 悬停零反馈复现 | `hover\j1-idle.png` 与 `j2-hover.png`（侧栏裁剪逐像素相同，均 166207 字节） |
| 回归现场 | `diag-now.png`（主内容区空白的原始现象） |

---

## 六、未解决风险

1. **§47 主页面切换 / Dev Panel / Update Log 三段动画视频 —— 无法产出。**
   本机**无任何视频编码器**（`ffmpeg` / `magick` / `gifski` 均不存在）。
   替代证据为运行期逐帧数值 + 静置帧截图。**本报告不声称 Motion 已通过视频验收。**
2. **§12 / §13 Badge 共享 Style 与材质精修未做。**
   `PCMigNumericBadgeBaseStyle` 等三个键**全项目 0 命中**，四页仍是内联 34×34、侧栏 32×32 —— 架构上确有重复。
   未做理由：纯架构整理、**零视觉收益**（视觉结果已达标）、需改 4 个页面 XAML，
   在已完成大量改动后属高风险低收益。**建议先与用户确认再动。**
3. **侧栏悬停反馈未解决**（已穷尽 3 种方案并回退，根因未定位）。
4. **DPI 125% / 150% 未实测**（等授权）。
5. **1440×900 视口下，Step2 安全提示与 Step4 日志末行会被视口底边切掉半行。**
   确定性：滚动可看全（已在 1440×1100 证实内容本身完整），**无信息丢失**。是否要改成卡片内滚动需用户决策。
6. **WinUI 未进发版链路**：`PCMig.sln` 未含 WinUI，`release.ps1` 仍只打包 WPF Gui/CLI。纳入需**单独授权**。
7. **`tools\uishot.ps1` 与 `lab\*.ps1` 缺 UTF-8 BOM**（死律 8）。
   本轮已修我新增的 `archive\scripts\winui-motion-burst.ps1`；
   但 `tools\uishot.ps1` 属"发版与验证脚本"，**按 AGENTS.md 三点五需用户当轮明确指令才能改**，故只报告未改。

---

## 七、备份与可回退性

- 精修前标签 `v0.5.0-before-final-polish-20260927`，源码镜像 + `.bundle` + 清单（18 项 SHA256 MATCH）齐全。
- ⚠ `archive\` 不在 git 内，`Themes\`/`Views\`/`Presentation\*.cs` 多为未跟踪文件 ——
  `git clean -fd` 会瞬间销毁全部成果（死律 9 已禁止）。
- 逐项回退方法见交接文档第七节。
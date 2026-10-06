# smoke-data\ —— 小型快速回归数据集（指针）

> 本目录在**仓库内**只放说明；数据集本体在仓库外的实验室根，避免把二进制塞进 git。

## canonical 位置

```
<实验室根>\smoke-data\     57.23 MB / 179 文件（≤ 100 MB ✓）
```

来源：由 `<实验室根>\Staging\payload\`（179 文件）复制而来（2026-10-06 归一整，按用户执行书 §8「小型测试数据只留一套」）。
原 `Staging\payload\` 仍作为生成器的默认输出目录保留。

## 覆盖的测试形态（§8 要求）

| 形态 | 用途 |
|---|---|
| 小文件 | 文件数压力、进度粒度、Scan 吞吐 |
| 多层目录 | 目录树遍历、计划生成、相对路径还原 |
| 中文文件名 / 中文目录名 | 编码与显示、长路径边界 |
| 空目录 | 是否被正确识别与保留 |
| 只读文件 | 属性继承、覆盖失败路径 |
| 合法长路径 | `\\?\` 前缀与 260 字符边界 |
| 一个较大样本 | 大文件通道（Large）触发 |
| 特殊扩展名 | 过滤规则、类型统计 |

## 用途

供 **Scan → Plan → Transfer → Verify** 全链路快速回归（分钟级），以及：

- UI 冒烟（`lab\three-vm\runner\uia_q2.ps1` / `uia_q3.ps1`）
- 进度真值断言（`aid=TotalImmersiveProgress`、`completedBytes`）
- 暂停 / 恢复 / 停止连续性快检（`lab\three-vm\scenarios\pause-resume-continuity.ps1`）

## 不做什么

- **不承担** 42 GiB 级盘满 / 熔断场景 —— 那需要重新生成大 payload，见 `PCMig\lab\three-vm\scenarios\README.md` 第三节「数据重新生成」。
- **不承担** 真实域环境验收 —— 那需要三台 VM 起来（`LAB-DC01` / `LAB-SRC01` / `LAB-DST01`，当前均 Off）。

## 纪律

- 数据集本体**不进 git**；只提交本说明。
- 若要扩充夹具，请改 `<实验室根>\Staging\ctl\labfile.ps1` 并同步更新本表；扩充后总体积仍**必须 ≤100 MB**。
- 生成脚本、测试参数、结果摘要必须保留（§7 / §12：删数据、留能力与证据）。
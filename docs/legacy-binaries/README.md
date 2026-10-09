# PCMig 历史版本二进制归档（Legacy Binary Archive）

本目录是 **2026-10-09 历史版本补归档** 的证据锚点，位于分支 `archive/legacy-binaries-20261009`。

## 这里的 Tag 是什么意思

每个历史版本对应一个注解 Tag：`legacy-binary/vX.Y.Z`（例如 `legacy-binary/v0.1.0`）。

- Tag 的语义是「**2026-10-09 的历史二进制验真清单锚点**」：
  它锚定的是该版本安装包与更新日志的字节级指纹，**不是**该历史软件当年发布时的源码快照。
- 所有 `legacy-binary/*` Tag 都指向本分支上的同一个清单提交。
- 不存在于本命名空间的历史开发 Tag（如 `v0.4.5`、`v0.4.6`、`v0.5.0`、`v0.5.1`、`v0.5.3` 等）一律保持原状，未被移动或重建。

## 重要提醒

GitHub 在每条 Release 下自动附带的 `Source code (zip/tar.gz)`，是本**归档清单提交**的源码快照，
并不是 PCMig vX.Y.Z 当年的源码。多数历史版本（v0.1.0–v0.4.9 中的绝大多数）**没有**确切的 Git 快照，
只有安装包这一份不可伪造的发布证据；本清单的「证据等级」列如实标注了这一点。

## 文件

| 文件 | 说明 |
|---|---|
| `MANIFEST-OF-TRUTH-49.tsv` | 49 个历史版本的唯一事实来源（版本 / 原始发布日 / UI 世代 / 安装包路径与字节 / SHA256 / PE 版本 / 证据等级 / 日志文件） |
| `LEGACY-INSTALLERS-SHA256-20261009.txt` | 与交付区一致的 10 字段管道格式清单（可直接用于逐文件校验） |
| `HISTORICAL-VERSIONS.md` | 人类可读的历史版本总索引（UI 世代、稳定基线候选、证据级别） |

## 复算方法

```powershell
Get-FileHash .\PCMigSetup-0.1.0.exe -Algorithm SHA256
# 与 MANIFEST-OF-TRUTH-49.tsv / LEGACY-INSTALLERS-SHA256-20261009.txt 中的值逐位比对
```

当前正式稳定版永远是 **v0.5.3**；历史版本仅代表当时状态，不保证适配现在的环境。
## 勘误（2026-10-09）

- 首次清单提交（23cde6a06724d8ca6c4966a3279e31c8d1e02774，也是 49 个 legacy-binary/vX.Y.Z Tag 指向的提交）
  里的 MANIFEST-OF-TRUTH-49.tsv 有两处不完整：单版日志的文件名少了  前缀、ChangelogSHA256 列是空的。
- 本提交只补这两列（以及 SumsName 的  前缀）。**安装包的字节与 SHA256 一个都没变**，它们在补发前已逐包独立复算并与本地权威原件 49/49 一致。
- 为避免移动任何已推送的 Tag，49 个 Tag 仍指向首次清单提交；Tag 注释里声明的是**安装包** SHA256，与本勘误无冲突。
- 阅读请以本分支最新提交的清单为准（下面 [MANIFEST-OF-TRUTH-49.tsv](MANIFEST-OF-TRUTH-49.tsv) 链接即最新版）。
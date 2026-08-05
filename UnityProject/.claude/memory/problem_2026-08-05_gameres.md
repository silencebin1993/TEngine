# GameRes 内容仓路径变更（2026-08-05）

## 问题现象

旧文档 / 旧习惯仍写 `Assets/AssetRaw`、`Assets/AssetArt`。这两顶层目录**已删除**；继续往那里写资源或改 Collector 会失败。

## 正确布局

- 唯一内容根：`Assets/GameRes/`（TEngine 嵌套 submodule，名 `GameRes`）
- 源文件：`Assets/GameRes/Art/`
- 热更/YooAsset：`Assets/GameRes/Raw/`（**只收集 Raw**）
- 不上 GitHub；bare：`F:/Project/BinGames-Art.git`

## 文档位置（已同步）

| 位置 | 说明 |
|------|------|
| `DesignDocs/Art_Repo_And_HotUpdate.md` | **权威全文**（含 AI 硬约束 §0） |
| 仓库根 `CLAUDE.md` §1–§2、§6 | 工作台入口摘要 |
| `UnityProject/CLAUDE.md` References | 工程入口提示 |
| `tengine-dev/references/{architecture,resource-api,luban-config,...}.md` | 路径已改为 GameRes/Raw |
| `luban-dev` skill / 导表 bat | DATA_OUTPATH → GameRes/Raw/Configs/bytes |

## 建议修正（给后人）

发现任何文档仍写 `AssetRaw`/`AssetArt` 顶层路径时，改为 `GameRes/Raw` 或 `GameRes/Art`，并指向 `Art_Repo_And_HotUpdate.md`。

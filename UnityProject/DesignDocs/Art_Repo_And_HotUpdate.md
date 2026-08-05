# 内容仓 GameRes 与热更分层

> 2026-08-05：`Art` + `Raw` 合并为**同一个**本地 submodule `Assets/GameRes/`，不上 GitHub。

## 结构

```
Assets/GameRes/          ← submodule（F:/Project/BinGames-Art.git）
  Art/                   ← 源文件（不进 YooAsset）
  Raw/                   ← 运行时 / 热更（YooAsset 只收集这里）
```

| 路径 | 上 GitHub？ | YooAsset |
|------|-------------|----------|
| `GameRes/Art` | ❌（内容仓本地/NAS） | 不收集 |
| `GameRes/Raw` | ❌（同上） | **全部收集** |
| TEngine 代码 | ✅ | — |

导出到 Raw **不会**进 GitHub——和 Art 在同一仓。

## 日常

```bash
cd TEngine && git -c protocol.file.allow=always submodule update --init --recursive

# 改资源
cd UnityProject/Assets/GameRes
git add -A && git commit -m "content: ..." && git -c protocol.file.allow=always push
cd ../../../..
git add UnityProject/Assets/GameRes && git commit -m "chore: bump GameRes"
```

迁 NAS：`BinGames/tools/set-art-remote.ps1 <新url>`（会改 GameRes 的 origin + `.gitmodules`）。

## AI 工具链

不修改 ECC/CCGS/hermes/codegraph。规范仍以 `TEngine/UnityProject/` 为准；资源路径记 `GameRes/Raw`。

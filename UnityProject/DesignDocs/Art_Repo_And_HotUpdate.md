# 内容仓 GameRes 与热更分层

> **状态**：已落地（2026-08-05）  
> **读者**：后续 Claude / Cursor / 其它 AI —— 写资源、配 YooAsset、跑 Luban 前先读本节。

## 0. 给 AI 的硬约束（先读）

1. **不要再使用** `Assets/AssetRaw/`、`Assets/AssetArt/` 作为顶层路径（已删除）。
2. **唯一内容根**：`Assets/GameRes/`（git submodule，本地 bare：`F:/Project/BinGames-Art.git`）。
3. **YooAsset 只收集** `Assets/GameRes/Raw/**`；`Art/` 永不进热更包。
4. **GameRes 不上 GitHub**（与 TEngine 代码仓分离）。往 `Raw` 导出 ≠ 上传 GitHub。
5. **不要**把 GameRes 装进 ECC / CCGS / hermes / codegraph；AI 工具链目录与内容仓无关。
6. Luban bytes 输出目录：`Assets/GameRes/Raw/Configs/bytes/`（见 `Configs/GameConfig/gen_code_bin_to_project.bat`）。
7. 图集：散图 `GameRes/Raw/UIRaw/Atlas|Raw`，AtlasMaker 输出 `GameRes/Raw/UIRaw/SpriteAtlas`。
8. HybridCLR DLL 文本资源路径：`GameRes/Raw/DLL`（`UpdateSetting.AssemblyTextAssetPath`）。

权威路径表也写在仓库根 `CLAUDE.md` §1–§2 与 `tengine-dev/references/resource-api.md`、`architecture.md`。

## 1. 结构

```
Assets/GameRes/                    ← submodule「GameRes」
  Art/                             ← 源：母带 / PSD / 购买包 / 设计稿
    Source/{Characters,Environments,UI,VFX,Audios,Animations,Fonts}/
    ThirdParty/
    Docs/
  Raw/                             ← 运行时成品（YooAsset CollectPath 全在这里）
    Actor|Audios|Configs|DLL|Effects|Fonts|Materials|Scenes|Shaders|UI|UIRaw
```

| 路径 | 上 GitHub？ | YooAsset |
|------|-------------|----------|
| `GameRes/Art` | ❌ 内容仓本地/NAS | 不收集 |
| `GameRes/Raw` | ❌ 同上 | **全部收集** |
| TEngine 代码 / `GameScripts` | ✅ | — |

## 2. 开发怎么放资源

| 你在做什么 | 放到 |
|------------|------|
| 母带、设计稿、商店原包 | `GameRes/Art/...` |
| Prefab、切图、音频成品、Luban bytes、图集产物 | `GameRes/Raw/...` |
| 临时白模、没有母带的小资源 | 可只放 `Raw/`，不必先过 Art |

**不是**「Art 整树复制到 Raw」，而是导出/制作成品进 Raw；Art 与 Raw 在同一 submodule 里分别 commit。

## 3. Git 工作流

```bash
# 拉内容
cd TEngine
git -c protocol.file.allow=always submodule update --init --recursive

# 改资源后
cd UnityProject/Assets/GameRes
git add -A && git commit -m "content: ..."
git -c protocol.file.allow=always push
cd ../../../..   # TEngine 根
git add UnityProject/Assets/GameRes && git commit -m "chore: bump GameRes"
# 再回 BinGames 根更新 TEngine 指针
```

迁 NAS / 私有远程（在 BinGames 根）：

```powershell
.\tools\set-art-remote.ps1 git@HOST:org/BinGames-GameRes.git
```

## 4. 历史迁移备忘（防 AI 读旧文档走错）

| 旧路径（已废） | 新路径 |
|----------------|--------|
| `Assets/AssetArt/` | `Assets/GameRes/Art/` |
| `Assets/AssetRaw/` | `Assets/GameRes/Raw/` |
| `Assets/AssetArt/Atlas/`（旧图集输出） | `Assets/GameRes/Raw/UIRaw/SpriteAtlas/` |
| Luban `.../Assets/AssetRaw/Configs/bytes/` | `.../Assets/GameRes/Raw/Configs/bytes/` |

TEngine 内 `.gitmodules` 子模块名：`GameRes`（不再是 `AssetArt`）。

## 5. 相关文件索引

| 文件 | 作用 |
|------|------|
| `TEngine/.gitmodules` | GameRes url（当前本机 bare） |
| `Assets/Editor/AssetBundleCollector/AssetBundleCollectorSetting.asset` | CollectPath → `GameRes/Raw/...` |
| `ProjectSettings/AtlasConfiguration.asset` | 图集输入/输出 |
| `Assets/TEngine/Settings/UpdateSetting.asset` | `AssemblyTextAssetPath: GameRes/Raw/DLL` |
| `Configs/GameConfig/gen_code_bin_to_project*.bat/sh` | Luban DATA_OUTPATH |
| `BinGames/tools/set-art-remote.ps1` | 改内容仓 remote |
| `Assets/GameRes/README.md` | 内容仓内说明 |

# 美术仓库与热更资源分层

> 实现状态：已落地（2026-08-05）。美术源仓库在 `F:/Project/BinGames-Art`，以 submodule 挂到 `Assets/AssetArt/`。

## 分层

| 路径 | 仓库 | YooAsset | 说明 |
|------|------|----------|------|
| `Assets/AssetArt/` | **BinGames-Art** submodule | 不收集 | 源美术、购买包、设计稿 |
| `Assets/AssetRaw/` | TEngine | **全部收集** | 运行时 / 热更资源 |

图集输出已改为 `Assets/AssetRaw/UIRaw/SpriteAtlas/`（原 `AssetArt/Atlas`），并已加入 Collector。

## 日常

```bash
# 拉齐美术
cd TEngine && git submodule update --init --recursive

# 改美术后提交
cd UnityProject/Assets/AssetArt
git add -A && git commit -m "art: ..." && git push
cd ../../../..   # 回到 TEngine 根
git add UnityProject/Assets/AssetArt && git commit -m "chore: bump AssetArt"
```

## 迁 NAS / 云

见 `Assets/AssetArt/README.md`（即 BinGames-Art README）。只改 art 仓 remote + `.gitmodules` url。

## 不碰的范围

本方案不修改 `.claude/`、ECC、CCGS、hermes、codegraph。AI 工具链仍只认 `TEngine/UnityProject/` 的代码规范。

# TEngine Luban 配置示例

本目录为 Luban 导出脚本示例（Windows 使用**管理员 PowerShell** `.ps1`，勿用 cmd / `.bat`）。

```
tengine-project/
├── gen_code_bin_to_project.ps1         # 客户端导出脚本（懒加载模板，推荐）
├── gen_code_bin_to_server.ps1          # 服务端导出脚本
└── ...
```

## 与正式工程对应

| 示例脚本 | 正式路径 |
|---|---|
| `gen_code_bin_to_project.ps1` | `Configs/GameConfig/gen_code_bin_to_project_lazyload.ps1`（或标准 `gen_code_bin_to_project.ps1`） |
| `gen_code_bin_to_server.ps1` | `Configs/GameConfig/gen_code_bin_to_server.ps1` |

## 运行（正式工程）

```powershell
cd Configs/GameConfig
$env:AI_MODE = '1'
powershell -NoProfile -ExecutionPolicy Bypass -File .\gen_code_bin_to_project_lazyload.ps1

powershell -NoProfile -ExecutionPolicy Bypass -File .\gen_code_bin_to_server.ps1
```

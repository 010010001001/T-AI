目的：小型化，在32G运存、512G固态、8G显存3060显卡上进行本地部署并运行。
上下文长度仅由显存限制，达到上限后进行一次内容截取，
原本采用Mini CPM5-2B, Q4_K_M量化，由于下载失败，采用AVA。

说明：本仓库为源码快照，不含 GGUF 模型、staging 二进制与 llama.cpp 源码。
构建：进入 ai-runtime，运行 build_windows.ps1；随后双击
ai-runtime\staging\ai-runtime-windows-x64\start.bat 启动。

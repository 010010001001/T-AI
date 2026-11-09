目的：小型化，在32G运存、512G固态、8G显存3060显卡上进行本地部署并运行。
上下文长度仅由显存限制，达到上限后进行一次内容截取，
原本采用Mini CPM5-2B, Q4_K_M量化，由于下载失败，采用AVA
启动路径：ai-runtime\staging\ai-runtime-windows-x64中的start.bat文件，双击启动
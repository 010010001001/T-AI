# AI Runtime（源码仓库）

本地部署的小型化 AI 运行环境：C++（llama.cpp）推理、C++ 确定性逻辑引擎、
C# 对话/权限/工具编排，支持 CUDA，目标硬件为 8 GB 显存 + 32 GB 内存。

> 本仓库只包含**源码与文档**。二进制、GGUF 模型（AVA-v2-Q4_K_M，约 1.2 GB）、
> `staging/` 运行目录和 `third_party/llama.cpp` 均未上传，需要在本地构建。

## 获取依赖

```powershell
git clone https://github.com/ggerganov/llama.cpp.git ai-runtime/third_party/llama.cpp
```

模型放到 `ai-runtime/models/AVA-v2-Q4_K_M.gguf`（或任意 `*.gguf`，程序自动发现）。

## 构建与运行

```powershell
cd ai-runtime
powershell -ExecutionPolicy Bypass -File .\build_windows.ps1
.\staging\ai-runtime-windows-x64\start.bat
```

构建需要：Windows x64、Visual Studio Build Tools（C++ 工作负载）、CMake、Ninja、
Git、.NET 8 SDK；可选 CUDA Toolkit（脚本自动检测架构并复制运行库）。

## 冒烟测试

```powershell
pwsh -File .\staging\ai-runtime-windows-x64\smoke_test.ps1 `
     -Root .\staging\ai-runtime-windows-x64
```

## 文档

完整架构、权限、记忆压缩与已知限制见
[ai-runtime/README.md](ai-runtime/README.md)。

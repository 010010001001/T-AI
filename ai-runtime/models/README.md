# 模型目录

当前使用的模型是 **AVA v2**（Qwen3.5-2B 的 QLoRA 合并版），Q4_K_M GGUF：

```
models/AVA-v2-Q4_K_M.gguf
```

来源：<https://hf-mirror.com/NAME0x0/AVA-v2-GGUF>（HuggingFace 镜像）。
运行时会自动加载 `models/` 下第一个非空的 `.gguf`，文件名不固定；
0 字节占位文件会被跳过。缺失模型时只有开放域与工具调用路径不可用，
符号计算、记忆压缩、回退链路仍可工作。

`封装/models/AVA-main/` 是 AVA 的源码仓库（训练/评测代码、语料与配置），
**不包含可被 llama.cpp 加载的权重**；不要把该目录当作模型路径。

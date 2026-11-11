# AI Runtime（Windows x64）

本地部署的小型化 AI 程序：C++ 推理 + C++ 确定性逻辑引擎，C# 负责对话、会话状态与工具调度，
插件以 C ABI 扩展。目标硬件为 RTX 3060 8G / 32G 内存 / 512G 固态。

## 1. 设计边界

| 层 | 语言 | 职责 | 不做什么 |
|----|------|------|----------|
| 推理层 `ai_inference.dll` | C++ | 加载 llama.cpp + GGUF，整批 `ai_generate`/`ai_count_tokens` | 不解析业务 JSON、不管理会话 |
| 逻辑层 `logic_engine.dll` | C++ | 意图路由、关键字提取、符号裁决、一致性检查 | 不做 token 生成 |
| 符号引擎 `sympy_engine.dll` | C | 中缀表达式求值 + 构建期 SymPy 模板 | 不携带 Python 运行时 |
| 对话层 `AiRuntime.exe` | C# | 会话状态机、记忆压缩、工具调度、插件加载、CLI | 不介入逐 token 推理循环 |
| 插件 | C ABI | 检索/文件/API 等副作用能力 | 不直接驱动模型 |

`ai_generate(prompt, params, callback, user_data)` 与 `ai_extract_keywords(text)` 都是"整批调用"：
所有 token 生成与词表扫描在原生侧完成，C# 只收流式回调或整段 JSON 结果。

## 2. 路由策略（条件化，而非统一符号化）

原生层 `ai_route` 用确定性词表判定，优先级 **TOOL > SYMBOLIC > OPEN**：

- **SYMBOLIC**：确定性翻译器（`DeterministicTranslator`）优先把"3 加 5 乘以 2"翻成表达式；
  翻译结果必须通过 `ai_verify_candidate`（语法校验 + 符号运算）才被采纳。LLM 只在确定性
  路径覆盖不了时提出候选表达式，且最终值只取符号引擎的结果。
- **TOOL**：LLM 只提取 `{plugin, function, args}`，执行由 C# 调用插件完成。
- **OPEN**：LLM 生成后经 `ai_consistency_check` 做形式化矛盾检测；不通过则撤回。

中文口述算式按**从左到右**结合（"3 加 5 乘以 2" = `(3+5)*2` = 16），这是产品定义；
需要标准优先级的用户可直接输入显式算式 `3+5*2`。

## 3. 记忆与"眨眼"压缩

- 每累计 **1000 token** 触发一次眨眼：把该段原文替换为确定性摘要（不调用 LLM）；
- 尾巴不足 1000 token 的块保留，等下一轮凑满；
- 压缩后活跃上下文仍超过 2000 token 时循环继续提取；
- 摘要写入 `memory_index`，级别 L1（日）→ L2（月）→ L3（年）→ L4（3/5/10 年），
  阈值分别为 3000 / 6000 / 12000 token（当前按级别整体合并；按 3/5/10 年窗口细分
  属于下一阶段，见"已知限制"）；
- 摘要会**实时回填当前会话**：压缩完成的下一轮提示词就包含"长期记忆摘要"，
  无需重启；上升到 L2-L4 时低层摘要被高层摘要取代，避免重复与无限膨胀；
- 摘要对应的原文保留在 `raw_dialogue`；`memory_index.raw_start_id/raw_end_id`
  是回退链路的唯一凭据；
- 回退时按失败关键字用 FTS5 找回条目，拉取**完整原文**，重新翻译并让符号引擎裁决，
  只回退一层，避免递归。

摘要示例：
> 17号执行了夹具模具的计算，搜索了家具，评价软件不好用

（时间/动作/实体三元组由 `keyword_tables.cpp` 的领域词表驱动，部署时应替换实体词表。）

## 4. 目录

```
ai-runtime/
├─ CMakeLists.txt                 # 根构建：sympy / logic / inference / 插件
├─ build_windows.ps1              # MSVC + Ninja + CUDA 一键构建与打包
├─ smoke_test.ps1                 # 端到端冒烟测试
├─ models/                        # GGUF（随安装包附带）
├─ rget/src/
│  ├─ inference/ai_inference.cpp  # llama.cpp 推理引擎
│  ├─ logic/                      # 路由 / 关键字 / 符号 / 一致性
│  └─ AiRuntime/                  # C# 对话层
└─ 封装/
   ├─ include/                    # 公开 C ABI 头文件
   ├─ generated/                  # sympy2c 生成产物（dispatch + templates）
   ├─ data/schema.sql             # SQLite schema（权威定义）
   └─ plugins_src/                # 示例插件 + ABI 不匹配测试插件
```

## 5. 构建前置

- Windows 10/11 x64，Visual Studio 2022/2026 Build Tools，勾选“使用 C++ 的桌面开发”
- CMake ≥ 3.20、Ninja、Git、.NET 8 SDK
- 运行冒烟测试需要 PowerShell 7（脚本含 `#requires -Version 7.0`）
- 可选：CUDA Toolkit（与驱动匹配）。无 `nvcc` 时自动退化为 CPU-only 构建
- `git submodule update --init --recursive` 拉取 `third_party/llama.cpp`
- GGUF：`models/AVA-v2-Q4_K_M.gguf`（AVA v2，基于 Qwen3.5-2B 的 QLoRA 合并模型；
  来自 `hf-mirror.com/NAME0x0/AVA-v2-GGUF`。模型缺失时 LLM 路径降级，
  其余确定性路径仍可用）

```powershell
# Windows PowerShell 默认禁止运行未签名脚本，需要一次性绕过
powershell -ExecutionPolicy Bypass -File .\build_windows.ps1

# 产物已包含运行时，可直接用 PowerShell 7 跑冒烟测试
pwsh -File .\staging\ai-runtime-windows-x64\smoke_test.ps1 `
     -Root .\staging\ai-runtime-windows-x64
```

本机验证（2026-11-10，CUDA 13.5 + RTX 5070 Laptop 8G）：

- 原生层（llama.cpp CUDA sm_120 + sympy_engine + logic_engine + ai_inference + 插件）编译通过；
- C# 层 `dotnet publish -r win-x64 --self-contained` 通过；
- CUDA 后端成功加载：`load_backend: loaded CUDA backend`，模型各层
  `assigned to device CUDA0`；`--smoke generate --max-tokens 128` 耗时约 238 ms；
- 冒烟测试 **35 项全部通过**（模型加载、路由、符号计算 16、眨眼压缩、回退、工具调用、
  插件 ABI、512 token 生成、清单校验）；
- 此前 CPU-only 构建同样 35/35 通过，可离线降级运行。
- 新增能力实测：
  - 图像 OCR：`test_assets/ocr_test.png` 识别为 "图像识别 OCR 测试 123"；
  - 工具路由：聊天中给出图片路径可触发 OCR，并返回识别文字；
  - 权限：关闭后非交互调用被拒绝（exit 3），保持后跨进程生效，超 30 分钟状态自动转为关闭；
  - 跨天持久化：新会话写入后在新进程恢复，问"我叫什么名字"能答出上一轮内容；
  - 按序号/时间戳恢复：`--restore-session 2`、`--restore-session 19:34` 均命中正确会话。
- CPU-only 下模型加载约 1.1 s，生成 512 token 可正常完成；
- `封装/models/AVA-main/` 是 AVA 训练/评测源码仓库（含占位 LFS 指针），
  不包含可加载的 GGUF；模型文件请使用 `models/AVA-v2-Q4_K_M.gguf`。

诊断子命令（无需模型）：

```powershell
AiRuntime.exe --smoke route    --input "搜索一下今天家具行业的新闻"
AiRuntime.exe --smoke translate --input "计算 3 加 5 乘以 2 的结果"
AiRuntime.exe --smoke keywords --input "17号执行了夹具模具的计算，搜索了家具"
AiRuntime.exe --smoke chat     --input "计算 3 加 5 乘以 2 的结果"
```

## 6. 显存与内存

- 构建脚本自动检测 `nvcc`（PATH / CUDA_PATH / 默认安装目录），并按 GPU 的
  compute capability 选择 `GGML_CUDA_ARCHITECTURES`（如 12.0 → sm_120）；
- 默认 `AI_GPU_LAYERS=99`（全部层上卡）。需要限制显存时设置 `AI_GPU_LAYERS=12`
  或更低，退化为部分卸载；
- CUDA 运行库（`cudart64_*`、`cublas64_*`、`cublasLt64_*`）由构建脚本复制到
  `native/`；缺少时 ggml 会静默回退 CPU，日志里不会再出现 `loaded CUDA backend`；
- 显存不作为硬性验收：冒烟测试打印加载/生成期间的显存读数，超过参考水位
  （默认 3072 MB）只告警。内存工作集仍断言 ≤ 28 GB。

## 7. 已知限制

- `ai_inference.cpp` 按 llama.cpp 较新的 C API 编写（`llama_model_get_vocab`、
  `llama_sampler_chain`、`ggml_backend_load_all*`）。使用旧版 llama.cpp 需要适配。
- 取消只阻止回调继续接收 token，不中断原生生成循环。
- 一致性校验只覆盖"同一谓词极性与否定并存"的文本内矛盾；与知识库事实的交叉校验
  需要 C# 层查询 `knowledge_facts`（表已建好，规则待扩展）。
- 确定性关键字提取只识别词表内的实体；词表外实体不会进入摘要。
- 月/年/多年的上卷目前是"整级合并"，尚未按日历窗口（具体月份、3/5/10 年区间）
  分组；`memory_index.timestamp` 已保留，可直接用于下一阶段的窗口化改造。
- OCR/语音以托管工具实现（非 C ABI 插件）：OCR 依赖 Windows 语言包，语音依赖麦克风
  与 zh-CN 识别引擎；无麦克风时返回空文本而不是报错。
- 会话恢复只支持"单活动会话 + 按时间戳切换"，不支持多个窗口同时并行对话；
  摘要按 token 预算截断注入，日/周/月/年精度差异由 L1-L4 层级体现。

## 8. 本次修复记录

- 删除重复的 `LogicNative` / `InferenceClient` / `LoadedPlugin` / `DialogueTurn` 定义；
- 修正 `ConversationOrchestrator` 中未定义变量与 `DialogueRole` 误用；
- 压缩状态机改为进入-/恢复原状态，修正 `Generating -> Compressing -> Idle` 导致的后续状态非法；
- 补全 `MemoryRepository` 的 SQL、FTS5 触发器与 schema 自举；
- 补全 `PluginManager`：ABI 校验、描述解析、config、init、execute/destroy/free、拒绝计数；
- 补齐 `Program.cs` 的启动、`--smoke` 分发与交互式对话；
- 修复 `ai_inference.cpp`（原文件误粘贴了 C# 代码）并实现整批生成接口；
- 补全 `keyword_tables.h`、`ai_keywords.cpp` 的时间/动作/实体三元组摘要；
- `sympy_engine` 补上真正的中缀求值器与模板分发，统一 `sympy_free_string` 分配器；
- 新增 `ai_inference.h`、`keyword_tables.h`、`generated/sympy_templates.c`、插件源码与 CMake；
- 显存检查改为告警而非硬性断言（参考水位 3072 MB）；默认全量层上卡，可用 `AI_GPU_LAYERS` 限制。
- 修复确定性翻译器的双重自增：中文分隔符会吞掉后一个 token（"3 加 5" 只剩 "3"）；
- 修复插件导出：插件源码未加 `/utf-8` 时按 GBK 解码中文注释，宏与 `dllexport` 声明被吞，
  DLL 没有任何导出符号；
- 修复 `Marshal.GetDelegateForFunctionPointer<Func<int>>`（泛型委托不被允许）导致的插件拒绝；
- 修复项目文件 XML 注释中的 `--`、`ConversationSession` 重复状态分支、
  `RunCompressTestAsync` 缺少 `async`；
- `build_windows.ps1` 现在自动加载 MSVC 环境、定位 Ninja、兼容待重启的 Build Tools，
  并对脚本使用 UTF-8 BOM，避免 Windows PowerShell 5.1 按 GBK 解析中文；
- 构建脚本自动定位 `nvcc`、按 GPU compute capability 选择 CUDA 架构；
  同时把 `cudart/cublas/cublasLt` 复制到 `native/` 并在启动时预加载，
  修复“CUDA 已安装但静默回退 CPU”的问题；
- 修复模型工具调用只返回 `function`（缺少 `plugin`）时无法解析的问题：
  按插件名/函数名反查归属；并为“执行命令 / 写入文件”增加确定性解析，
  避免模型误选 echo；
- 工作区工具结果直接以确定性文本返回（写入路径/字节数、退出码、stdout），
  不再经模型改写；
- 修复连续会话的长期记忆断档：压缩摘要现在实时回填 `ConversationSession`，
  下一轮提示词即可见；高层摘要会替换低层摘要，并按 token 预算优先保留高层/新近内容。

## 9. 图像识别 / 语音识别与权限

两者以**托管工具**形式接入（`Tools/IManagedTool.cs`），与原生 C ABI 插件并列：

| 工具 | 实现 | 输入 | 输出 |
|------|------|------|------|
| `image_ocr` | Windows 自带 OCR（WinRT，离线） | `{"path":"C:\\...\\a.png"}` | `{"text":"...","lines":N,"language":"..."}` |
| `speech_asr` | Windows 自带 SAPI 识别（离线） | `{"language":"zh-CN","timeout_ms":15000}` | `{"text":"...","culture":"zh-CN"}` |

权限规则（`Security/ToolPermissionManager.cs`）：

- 首次调用会在控制台询问：`[y] 仅本次` / `[k] 保持开启（跨重启）` / `[n] 拒绝`；
- 仅本次：任务结束后 **30 分钟**无使用自动关闭；
- 保持：写入 `data/permissions.json`，跨进程重启仍有效，直到用户说关闭；
- 非交互环境（脚本/重定向）且未保持权限时直接拒绝，避免静默提权。

聊天中可直接用命令管理：

```
保持图像权限 / 关闭图像权限 / 保持语音权限 / 关闭语音权限 / 权限状态
```

也可脱离 LLM 直接调用：

```powershell
.\AiRuntime.exe --tool image_ocr --args "{\"path\":\"C:\\pics\\a.png\"}"
.\AiRuntime.exe --tool speech_asr --args "{\"language\":\"zh-CN\",\"timeout_ms\":10000}"
```

语音识别需要麦克风与已安装的 zh-CN 识别引擎；本机检测到 `MS-2052-80-DESK`。

## 10. 跨天会话持久化

- 启动时默认恢复**当前会话**（`data/session_state.json` 指向的会话，或最近一次会话）；
- 恢复范围：最近若干轮原文（约 1600 token）+ 所有层级摘要（约 900 token，超出时截断并提示
  "达到上下文上限"）；摘要会以"长期记忆摘要"注入开放域提示词；
- `新开启一轮对话`：切换到新的空会话，旧会话保留在数据库中；
- 列出与恢复（按最后活跃时间倒序）：

```
列出对话
恢复对话 2                     # 序号
恢复对话 19:34                 # 时间戳片段
恢复对话 2026-11-10 19:33      # 完整时间戳片段
恢复对话 bd8a3aaa              # id 前缀
```

命令行等价形式：`--list-sessions`、`--restore-session <序号|时间戳|id>`、`--new-session`。

上限提示：

- 上下文：恢复时若摘要 + 原文 + 预留生成超过约 3500 token，会截断并明确提示；
- 存储：`AI_MAX_DB_MB`（默认 10240）与磁盘剩余 2 GB 阈值；
- 显存：`AI_MAX_VRAM_MB`（默认 7168）；仅在 `AI_GPU_LAYERS != 0` 且存在 `nvidia-smi` 时检查。

## 11. 自主工具：工作区三档权限

`workspace_files` / `workspace_edit` / `workspace_run` 三个托管工具，让模型可以
浏览、修改工作区文件并执行命令。

| 级别 | 能力 |
|------|------|
| 1（默认） | 只浏览工作区文件：`workspace_files.list/read` |
| 2 | 仅修改工作区文件：`workspace_edit.write/append`，路径必须位于工作区内 |
| 3 | 完全权限：读写任意路径，并以**当前用户**身份执行命令；不会自动提权 |

- 首次使用任一工具时，如果未指定工作区，会提示输入**绝对路径**并保存；
  非交互环境直接报错。选定工作区后权限默认回到 1。
- 修改权限：`权限 1|2|3`、`只读权限`、`编辑权限`、`完全权限`；查看：`工作区状态`。
- 执行命令需要权限 3；若命令返回“拒绝访问”或错误码 5，工具会明确提示需要管理员，
  并给出“以管理员身份打开终端 → cd 到工作区 → 手动执行”的步骤，不会尝试提权。
- 路径越界（`../`、绝对路径不在工作区内）在权限 1/2 下会被拒绝。
- 命令行等价形式：`--set-workspace <绝对路径>`、`--set-permission 1|2|3`、
  `--workspace-status`，以及直接调用
  `--tool workspace_edit --function write --args "{\"path\":\"a.py\",\"content\":\"...\"}"`。

实测：权限 1 下写入被拒；权限 2 可写工作区内文件、越界写被拒；权限 3 可执行
`echo`/`type` 等命令；`net session` 返回管理员权限提示与处理步骤；模型可通过
“在 test.py 里写入 …”“执行命令 type test.py”自主完成写入与运行。

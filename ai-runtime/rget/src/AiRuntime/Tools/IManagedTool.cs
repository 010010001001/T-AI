// src/AiRuntime/Tools/IManagedTool.cs
/// <summary>
/// 托管工具（内置插件）契约。与原生 C ABI 插件并列：
/// 识别/工作区类能力用托管实现更可靠，原生插件仍用于需要 C/C++ 的场景。
/// </summary>
public interface IManagedTool
{
    /// <summary>工具名，同时作为权限键（如 image_ocr / speech_asr / workspace_files）。</summary>
    string Name { get; }

    /// <summary>可调用的函数名列表。</summary>
    string[] Functions { get; }

    /// <summary>是否为敏感能力：执行前必须经过 PermissionManager 询问。</summary>
    bool RequiresPermission { get; }

    /// <summary>执行指定函数。输入/输出均为 JSON 字符串（与原生插件一致）。</summary>
    string Execute(string function, string inputJson);
}

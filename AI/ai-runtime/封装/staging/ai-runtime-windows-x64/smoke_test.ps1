#requires -Version 7.0
<#
.SYNOPSIS
    AI Runtime 端到端冒烟测试。
    依次验证：模型加载 → SYMBOLIC 路由 → TOOL 路由 → 1000-token 压缩
              → 回退链路 → 插件加载与执行 → 显存与内存快照。
    任何断言失败返回非零退出码。
#>
param(
    [string]$Root = $PSScriptRoot,
    [string]$RuntimeExe = "AiRuntime.exe",
    [string]$GgufName = "MiniCPM5-2B-Q4_K_M.gguf",
    [int]$VramLimitMb = 3072,          # 3GB 显存硬上限
    [int]$RamLimitGb = 28,             # 32GB 系统内存，留 4GB 给 OS
    [string]$LogDir = "$PSScriptRoot\smoke_logs"
)

$ErrorActionPreference = "Stop"
$script:failed = @()
$script:passed = @()

function Assert-True($condition, $name, $detail = "") {
    if ($condition) { $script:passed += $name; Write-Host "  [PASS] $name" -ForegroundColor Green }
    else { $script:failed += "$name $detail"; Write-Host "  [FAIL] $name $detail" -ForegroundColor Red }
}

function Assert-ExitCode($expected, $actual, $name) {
    Assert-True ($actual -eq $expected) $name "expected=$expected actual=$actual"
}

# ---------- 0. 前置检查 ----------
Write-Host "`n=== 0. 前置检查 ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$exePath = Join-Path $Root $RuntimeExe
Assert-True (Test-Path $exePath) "AiRuntime.exe 存在" $exePath

$ggufPath = Join-Path $Root "models\$GgufName"
Assert-True (Test-Path $ggufPath) "GGUF 存在" $ggufPath
if (Test-Path $ggufPath) {
    $ggufSizeMb = [math]::Round((Get-Item $ggufPath).Length / 1MB, 1)
    Write-Host "  GGUF 体积: $ggufSizeMb MB"
}

# manifest.sha256 校验（GGUF 与关键 DLL）
$manifestPath = Join-Path $Root "manifest.sha256"
if (Test-Path $manifestPath) {
    $bad = @()
    Get-Content $manifestPath | Where-Object { $_ -match '\S' } | ForEach-Object {
        $parts = $_ -split '\s+', 2
        $expected = $parts[0]; $file = Join-Path $Root $parts[1]
        if (-not (Test-Path $file)) { $bad += "缺失:$($parts[1])"; return }
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $expected) { $bad += "哈希不匹配:$($parts[1])" }
    }
    Assert-True ($bad.Count -eq 0) "manifest.sha256 校验" ($bad -join "; ")
} else {
    Write-Host "  [SKIP] manifest.sha256 不存在" -ForegroundColor Yellow
}

# nvidia-smi 可用性
$smiAvailable = $null -ne (Get-Command nvidia-smi -ErrorAction SilentlyContinue)
Assert-True $smiAvailable "nvidia-smi 可用" "未找到 nvidia-smi，显存检查将降级"

# ---------- 辅助：显存与内存快照 ----------
function Get-VramUsedMb {
    if (-not $smiAvailable) { return $null }
    # WDDM 下 --query-compute-apps=used_memory 可能返回 N/A，
    # 退回到 GPU 总量 used 作为代理指标
    $line = & nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>$null | Select-Object -First 1
    if ($line -match '^\s*(\d+)') { return [int]$Matches[1] }
    return $null
}

function Get-ProcessSnapshot {
    $p = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($RuntimeExe)) -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    return [pscustomobject]@{
        WorkingSetMb = [math]::Round($p.WorkingSet64 / 1MB, 1)
        PrivateMb    = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    }
}

# ---------- 1. 模型加载 ----------
Write-Host "`n=== 1. 模型加载 ===" -ForegroundColor Cyan
$loadLog = Join-Path $LogDir "01_load.log"
$loadSw = [Diagnostics.Stopwatch]::StartNew()
$loadOut = & $exePath --smoke load --model "models/$GgufName" 2>&1 | Tee-Object -FilePath $loadLog
$loadSw.Stop()
$loadRc = $LASTEXITCODE

Assert-ExitCode 0 $loadRc "模型加载退出码"
Assert-True ($loadOut -match 'MODEL_LOADED') "加载成功标记" ($loadOut -join "`n")
Write-Host "  加载耗时: $([math]::Round($loadSw.Elapsed.TotalSeconds, 2)) s"

# 加载后立即快照（模型已常驻）
Start-Sleep -Seconds 1
$vramAfterLoad = Get-VramUsedMb
$procAfterLoad = Get-ProcessSnapshot
if ($vramAfterLoad -ne $null) {
    Assert-True ($vramAfterLoad -le $VramLimitMb) "加载后显存 ≤ ${VramLimitMb}MB" "actual=${vramAfterLoad}MB"
} else {
    Write-Host "  [WARN] 显存读数不可用，跳过显存断言" -ForegroundColor Yellow
}

# ---------- 2. SYMBOLIC 路由 ----------
Write-Host "`n=== 2. SYMBOLIC 路由 ===" -ForegroundColor Cyan
$symLog = Join-Path $LogDir "02_symbolic.log"
# 选择一个构建时已覆盖的符号表达式，避免 UNSUPPORTED
$symPrompt = "计算 3 加 5 乘以 2 的结果"
$symOut = & $exePath --smoke chat --input $symPrompt 2>&1 | Tee-Object -FilePath $symLog
$symRc = $LASTEXITCODE

Assert-ExitCode 0 $symRc "SYMBOLIC 路由退出码"
# 断言：路由为 SYMBOLIC，且 LLM 的数值被丢弃，最终值来自符号引擎
Assert-True ($symOut -match 'route=SYMBOLIC') "路由判定为 SYMBOLIC" ($symOut -join "`n")
Assert-True ($symOut -match 'verdict_accepted=true') "符号引擎裁决通过" ($symOut -join "`n")
Assert-True ($symOut -match 'value=16') "最终值为符号引擎结果 16（3+5*2）" ($symOut -join "`n")
# 关键断言：日志中不出现 LLM 的原始数值作为最终答案
Assert-True ($symOut -notmatch 'llm_candidate_value=16.*accepted_value=16.*source=llm') "LLM 数值未被直接采纳" ""

# ---------- 3. TOOL 路由 ----------
Write-Host "`n=== 3. TOOL 路由 ===" -ForegroundColor Cyan
$toolLog = Join-Path $LogDir "03_tool.log"
# 触发工具调用；插件目录中须存在一个提供 "echo" 功能的示例插件
$toolPrompt = "搜索一下今天家具行业的新闻"
$toolOut = & $exePath --smoke chat --input $toolPrompt 2>&1 | Tee-Object -FilePath $toolLog
$toolRc = $LASTEXITCODE

Assert-ExitCode 0 $toolRc "TOOL 路由退出码"
Assert-True ($toolOut -match 'route=TOOL') "路由判定为 TOOL" ($toolOut -join "`n")
Assert-True ($toolOut -match 'plugin_execute_rc=0') "插件执行返回 0" ($toolOut -join "`n")
Assert-True ($toolOut -match 'llm_calls=2') "LLM 被调用恰好 2 次（提取参数+组织结果）" ($toolOut -join "`n")
# 断言：工具执行发生在 C# 层，日志中有区分标记
Assert-True ($toolOut -match 'executed_by=csharp') "工具执行位置为 C# 层" ($toolOut -join "`n")

# ---------- 4. 1000-token 压缩 ----------
Write-Host "`n=== 4. 1000-token 压缩 ===" -ForegroundColor Cyan
$compressLog = Join-Path $LogDir "04_compress.log"
# 注入一段约 1200 token 的对话文本，验证恰好触发一次压缩
$compressOut = & $exePath --smoke compress-test --target-tokens 1200 2>&1 | Tee-Object -FilePath $compressLog
$compressRc = $LASTEXITCODE

Assert-ExitCode 0 $compressRc "压缩测试退出码"
Assert-True ($compressOut -match 'blink_triggered=1') "恰好触发 1 次眨眼" ($compressOut -join "`n")
Assert-True ($compressOut -match 'sealed_chunks=1') "恰好密封 1 个完整块" ($compressOut -join "`n")
Assert-True ($compressOut -match 'tail_retained=true') "尾块被保留" ($compressOut -join "`n")
Assert-True ($compressOut -match 'memory_index_l1_count=1') "L1 条目数为 1" ($compressOut -join "`n")
# token 守恒：raw token 总数 ≈ 密封块 token + 尾块 token
Assert-True ($compressOut -match 'token_conserved=true') "token 守恒" ($compressOut -join "`n")

# ---------- 5. 回退链路 ----------
Write-Host "`n=== 5. 回退链路 ===" -ForegroundColor Cyan
$fallbackLog = Join-Path $LogDir "05_fallback.log"
# 构造一个"摘要丢失关键数值"的用例：
# L1 摘要写"计算了夹具模具"，原文含具体数值，回退应拉原文重新裁决
$fallbackOut = & $exePath --smoke fallback-test --keyword "夹具模具" 2>&1 | Tee-Object -FilePath $fallbackLog
$fallbackRc = $LASTEXITCODE

Assert-ExitCode 0 $fallbackRc "回退测试退出码"
Assert-True ($fallbackOut -match 'fallback_triggered=true') "回退被触发" ($fallbackOut -join "`n")
Assert-True ($fallbackOut -match 'raw_range_found=true') "FTS5 命中并定位到原文范围" ($fallbackOut -join "`n")
Assert-True ($fallbackOut -match 'reverify_accepted=true') "原文重新裁决通过" ($fallbackOut -join "`n")
# 断言回退只发生一层，不递归
Assert-True ($fallbackOut -match 'fallback_depth=1') "回退深度为 1（不递归）" ($fallbackOut -join "`n")

# ---------- 6. 插件加载与执行 ----------
Write-Host "`n=== 6. 插件加载与执行 ===" -ForegroundColor Cyan
$pluginLog = Join-Path $LogDir "06_plugin.log"
$pluginOut = & $exePath --smoke plugin-test 2>&1 | Tee-Object -FilePath $pluginLog
$pluginRc = $LASTEXITCODE

Assert-ExitCode 0 $pluginRc "插件测试退出码"
Assert-True ($pluginOut -match 'plugin_abi_version_ok=true') "ABI 版本校验通过" ($pluginOut -join "`n")
Assert-True ($pluginOut -match 'plugin_describe_ok=true') "plugin_describe 成功" ($pluginOut -join "`n")
Assert-True ($pluginOut -match 'plugin_init_ok=true') "plugin_init 成功" ($pluginOut -join "`n")
Assert-True ($pluginOut -match 'plugin_execute_ok=true') "plugin_execute 成功" ($pluginOut -join "`n")
Assert-True ($pluginOut -match 'plugin_destroy_called=true') "plugin_destroy 被调用" ($pluginOut -join "`n")
# 加载一个 ABI 版本不匹配的插件，必须被拒绝
Assert-True ($pluginOut -match 'abi_mismatch_rejected=true') "ABI 不匹配插件被拒绝" ($pluginOut -join "`n")

# ---------- 7. 显存与内存快照 ----------
Write-Host "`n=== 7. 显存与内存快照 ===" -ForegroundColor Cyan
$snapLog = Join-Path $LogDir "07_snapshot.log"

# 生成压力：一次 512 token 生成，观察峰值
$stressOut = & $exePath --smoke generate --input "用一句话解释什么是本地部署。" --max-tokens 512 2>&1
$stressRc = $LASTEXITCODE
Assert-ExitCode 0 $stressRc "512 token 生成退出码"

$vramPeak = Get-VramUsedMb
$procPeak = Get-ProcessSnapshot
if ($vramPeak -ne $null) {
    Assert-True ($vramPeak -le $VramLimitMb) "生成期间显存 ≤ ${VramLimitMb}MB" "peak=${vramPeak}MB"
}
if ($procPeak -ne $null) {
    $ramLimitMb = $RamLimitGb * 1024
    Assert-True ($procPeak.WorkingSetMb -le $ramLimitMb) "工作集内存 ≤ ${RamLimitGb}GB" "peak=$($procPeak.WorkingSetMb)MB"
    Assert-True ($procPeak.PrivateMb -le $ramLimitMb) "私有内存 ≤ ${RamLimitGb}GB" "peak=$($procPeak.PrivateMb)MB"
}

@"
SMOKE_SNAPSHOT
vram_after_load_mb = $vramAfterLoad
vram_peak_mb       = $vramPeak
proc_workingset_mb = $($procPeak.WorkingSetMb)
proc_private_mb    = $($procPeak.PrivateMb)
"@ | Set-Content (Join-Path $LogDir "snapshot.txt")

# ---------- 汇总 ----------
Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
Write-Host "通过: $($script:passed.Count)" -ForegroundColor Green
if ($script:failed.Count -gt 0) {
    Write-Host "失败: $($script:failed.Count)" -ForegroundColor Red
    $script:failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host "`n日志目录: $LogDir"
    exit 1
}
Write-Host "所有断言通过。日志目录: $LogDir" -ForegroundColor Green
exit 0
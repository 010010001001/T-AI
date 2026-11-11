param(
    [string]$BuildDir = "build",
    [string]$StagingDir = "staging",
    [string]$CudaArch = "auto",        # auto：按 GPU compute capability 推导（如 12.0 -> 120）
    [switch]$SkipPackage
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Push-Location $root
try {

# ---------- 1. 前置检查：不自动安装，要求用户显式满足 ----------
function Require-Command($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "缺少 $name。$hint"
    }
}
Require-Command "cmake"  "安装 CMake 3.20+ 并加入 PATH"
Require-Command "dotnet" "安装 .NET 8 SDK"
Require-Command "git"    "安装 Git"

if (-not (Test-Path "third_party/llama.cpp/CMakeLists.txt")) {
    throw "缺少 third_party/llama.cpp。请执行：git submodule update --init --recursive"
}

# 定位带 C++ 工作负载的 Visual Studio（2022 或更新）
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "未找到 Visual Studio Installer（vswhere.exe）" }

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath

# 某些安装（尤其离线/待重启的 Build Tools）不会被 -requires 命中，
# 退化为直接查找包含 VsDevCmd.bat 且带 MSVC 编译器的安装目录。
if (-not $vsPath) {
    $roots = @("$env:ProgramFiles\Microsoft Visual Studio",
               "${env:ProgramFiles(x86)}\Microsoft Visual Studio")
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $candidates = Get-ChildItem $r -Recurse -Filter VsDevCmd.bat -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\Common7\\Tools\\VsDevCmd\.bat$' }
        foreach ($candidate in $candidates) {
            $installRoot = Split-Path (Split-Path (Split-Path $candidate.FullName -Parent) -Parent) -Parent
            if (Test-Path (Join-Path $installRoot "VC\Tools\MSVC")) {
                $vsPath = $installRoot
                break
            }
        }
        if ($vsPath) { break }
    }
}

if (-not $vsPath) {
    throw "未找到带 C++ 工作负载的 Visual Studio。安装『使用 C++ 的桌面开发』后重试。"
}
$devCmd = Join-Path $vsPath "Common7\Tools\VsDevCmd.bat"
if (-not (Test-Path $devCmd)) { throw "缺少 VsDevCmd.bat：$devCmd" }

# 定位 Ninja：PATH 优先，其次 winget 安装目录
$ninja = (Get-Command ninja -ErrorAction SilentlyContinue).Source
if (-not $ninja) {
    $ninja = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" `
        -Filter ninja.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ninja) {
    throw "缺少 Ninja。执行：winget install --id Ninja-build.Ninja --exact"
}

# 定位 nvcc：PATH 优先，其次 CUDA_PATH / 默认安装目录
$nvcc = (Get-Command nvcc -ErrorAction SilentlyContinue).Source
if (-not $nvcc -and $env:CUDA_PATH) {
    $candidate = Join-Path $env:CUDA_PATH "bin\nvcc.exe"
    if (Test-Path $candidate) { $nvcc = $candidate }
}
if (-not $nvcc) {
    $nvcc = Get-ChildItem "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA" `
        -Filter nvcc.exe -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
$hasCuda = [bool]$nvcc
$cudaBin = if ($hasCuda) { Split-Path $nvcc } else { $null }
# CUDA 13 起运行库在 bin\x64；旧版在 bin
$cudaRuntimeDir = if ($cudaBin -and (Test-Path (Join-Path $cudaBin "x64"))) {
    Join-Path $cudaBin "x64"
} else { $cudaBin }

if ($hasCuda) {
    if ($CudaArch -eq "auto") {
        $cap = (& nvidia-smi --query-gpu=compute_cap --format=csv,noheader 2>$null | Select-Object -First 1)
        if ($cap -match '^\s*(\d+)\.(\d+)') { $CudaArch = "$($Matches[1])$($Matches[2])" }
        else { $CudaArch = "86" }
    }
    Write-Host "CUDA: $nvcc（架构 sm_$CudaArch）"
} else {
    Write-Warning "未找到 nvcc：将以 CPU-only 构建推理引擎（显存占用为 0，吞吐较低）。"
}

# ---------- 2. 在 VS 开发者环境中配置并构建原生层 ----------
$cmakeArgs = @(
    "-S", ".",
    "-B", "$BuildDir/runtime",
    "-G", "Ninja",
    "-DCMAKE_BUILD_TYPE=Release",
    "-DGGML_NATIVE=OFF",
    "-DGGML_BACKEND_DL=ON",
    "-DLLAMA_BUILD_TESTS=OFF",
    "-DLLAMA_BUILD_EXAMPLES=OFF",
    "-DLLAMA_BUILD_SERVER=OFF"
)
if ($hasCuda) {
    $cmakeArgs += "-DGGML_CUDA=ON"
    $cmakeArgs += "-DGGML_CUDA_ARCHITECTURES=$CudaArch"
}

# 批处理文件负责加载 MSVC 环境；PowerShell 直接调用 VsDevCmd.bat 不会保留环境变量。
$bat = Join-Path $env:TEMP "ai_runtime_build_$PID.bat"
$cmakeLine = ($cmakeArgs | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
}) -join ' '

@"
@echo off
call "$devCmd" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
set "PATH=$cudaBin;$(Split-Path $ninja);%PATH%"
cmake $cmakeLine
if errorlevel 1 exit /b 1
cmake --build "$BuildDir/runtime" --config Release
"@ | Set-Content -Encoding ASCII $bat

try {
    cmd /c "`"$bat`""
    if ($LASTEXITCODE -ne 0) { throw "原生层构建失败（exit=$LASTEXITCODE）" }
}
finally {
    Remove-Item $bat -Force -ErrorAction SilentlyContinue
}

# ---------- 3. 发布 C# 对话层（self-contained） ----------
dotnet publish "rget/src/AiRuntime" -c Release -r win-x64 --self-contained `
    -o "$StagingDir/ai-runtime-windows-x64" `
    -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# ---------- 4. 组装 staging ----------
$out = "$StagingDir/ai-runtime-windows-x64"
$outFull = (Resolve-Path $out).Path
New-Item -ItemType Directory -Force -Path `
    "$out/native", "$out/models", "$out/data", "$out/plugins" | Out-Null

# 自研原生 DLL（sympy_engine / logic_engine / ai_inference）
Copy-Item "$BuildDir/runtime/staging/native/*" "$out/native/" -Force -ErrorAction SilentlyContinue
# 示例插件
Copy-Item "$BuildDir/runtime/staging/plugins/*" "$out/plugins/" -Force -ErrorAction SilentlyContinue
# 插件目录只保留运行时 DLL 与配置
Get-ChildItem "$out/plugins" -Filter *.exp -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem "$out/plugins" -Filter *.lib -ErrorAction SilentlyContinue | Remove-Item -Force
# llama.cpp 运行时 DLL（ggml/llama）；GGML_BACKEND_DL 产物也放在 native/，
# AppHost 会在 backends/ 不存在时直接使用 native/，避免同一后端加载两份。
$llamaBin = "$BuildDir/runtime/bin"
if (Test-Path $llamaBin) {
    Copy-Item "$llamaBin/*.dll" "$out/native/" -Force -ErrorAction SilentlyContinue
}

# CUDA 运行库：ggml-cuda.dll 依赖 cublas/cudart；不复制会在加载时静默回退 CPU
if ($hasCuda -and $cudaRuntimeDir) {
    Get-ChildItem $cudaRuntimeDir -Filter *.dll -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(cublas64_|cublasLt64_|cudart64_)' } |
        ForEach-Object { Copy-Item $_.FullName "$out/native/" -Force }
}
# 旧 DLL 残留（.exp/.lib 不随包发布）
Get-ChildItem "$out/native" -Filter *.exp -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem "$out/native" -Filter *.lib -ErrorAction SilentlyContinue | Remove-Item -Force

# 种子数据库与脚本
if (Test-Path "封装/data/schema.sql") {
    Copy-Item "封装/data/schema.sql" "$out/data/" -Force
}
if (Test-Path "封装/data/knowledge_seed.db") {
    Copy-Item "封装/data/knowledge_seed.db" "$out/data/" -Force
}
Copy-Item "封装/staging/ai-runtime-windows-x64/start.bat" "$out/" -Force
Copy-Item "封装/staging/ai-runtime-windows-x64/install.ps1" "$out/" -Force
Copy-Item "smoke_test.ps1" "$out/smoke_test.ps1" -Force

# GGUF：安装包附带 models/ 下所有模型；缺少时给出明确指引
$models = Get-ChildItem "models/*.gguf" -File -ErrorAction SilentlyContinue | Where-Object { $_.Length -gt 0 }
if ($models) {
    foreach ($m in $models) { Copy-Item $m.FullName "$out/models/" -Force }
} else {
    Write-Warning "缺少 models/*.gguf；安装包将不包含模型，用户需自行放入 models/ 后再启动。"
}

# 插件配置
if (Test-Path "$out/plugins/echo_plugin.dll") {
    '{}' | Set-Content "$out/plugins/echo_plugin.json" -Encoding UTF8
}

# ---------- 5. 计算 manifest.sha256 ----------
# 必须写相对路径（native/...、models/...），安装与冒烟脚本都按 staging 根目录解析。
$manifest = @()
Get-ChildItem "$outFull/native/*.dll", "$outFull/models/*.gguf" -File -ErrorAction SilentlyContinue | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    $rel = $_.FullName.Substring($outFull.Length + 1).Replace('\', '/')
    $manifest += "$hash  $rel"
}
$manifest | Set-Content "$outFull/manifest.sha256" -Encoding UTF8

# ---------- 6. 打包 ----------
if (-not $SkipPackage) {
    $zip = "ai-runtime-windows-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$out/*" -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "安装包: $zip"
}

Write-Host "构建完成。staging 目录: $out"
}
finally {
    Pop-Location
}

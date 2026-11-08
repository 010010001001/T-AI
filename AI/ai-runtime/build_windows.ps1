param(
    [string]$BuildDir = "build",
    [string]$StagingDir = "staging",
    [string]$CudaArch = "86"   # RTX 3060 = SM 86；构建时固定，不依赖检测
)

# ---------- 1. 前置检查：不自动安装，要求用户显式满足 ----------
function Require-Command($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "缺少 $name。$hint"
    }
}
Require-Command "cmake"  "安装 CMake 3.20+ 并加入 PATH"
Require-Command "nvcc"   "安装 CUDA Toolkit 并与驱动版本匹配"
Require-Command "dotnet" "安装 .NET 8 SDK"

# 检查 MSVC 可用性（通过 vswhere 定位 VS 2022）
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "未找到 Visual Studio 2022" }

# ---------- 2. 构建 llama.cpp（CUDA + 动态后端） ----------
# 使用 Ninja Multi-Config；GGML_BACKEND_DL=ON 产出独立后端 DLL
cmake -S third_party/llama.cpp -B "$BuildDir/llama" -G "Ninja Multi-Config" `
    -DCMAKE_BUILD_TYPE=Release `
    -DGGML_CUDA=ON `
    -DGGML_BACKEND_DL=ON `
    -DGGML_NATIVE=OFF `
    -DGGML_CUDA_ARCHITECTURES=$CudaArch `
    -DBUILD_SHARED_LIBS=ON
cmake --build "$BuildDir/llama" --config Release

# ---------- 3. 复制预编译符号模块（不在本机运行 sympy2c） ----------
# sympy_engine.dll 由 CI 的受控 MSVC 构建机产出，放入 prebuilt/
if (-not (Test-Path "prebuilt/sympy_engine.dll")) {
    throw "缺少 prebuilt/sympy_engine.dll。该产物必须由 CI 符号模块构建环节提供。"
}

# ---------- 4. 构建逻辑引擎 ----------
cmake -S . -B "$BuildDir/runtime" -G "Ninja" `
    -DCMAKE_BUILD_TYPE=Release `
    -DSYMPY_ENGINE_DLL="$(Resolve-Path 'prebuilt/sympy_engine.dll')"
cmake --build "$BuildDir/runtime"

# ---------- 5. 发布 C# 对话层（self-contained） ----------
dotnet publish src/AiRuntime -c Release -r win-x64 --self-contained `
    -o "$StagingDir" `
    -p:PublishSingleFile=false

# ---------- 6. 组装 staging ----------
New-Item -ItemType Directory -Force -Path "$StagingDir/native","$StagingDir/backends","$StagingDir/models","$StagingDir/data","$StagingDir/plugins" | Out-Null

Copy-Item "$BuildDir/llama/bin/Release/*.dll" "$StagingDir/native/" -Force
Copy-Item "$BuildDir/llama/bin/Release/*.dll" "$StagingDir/backends/" -Force
Copy-Item "$BuildDir/runtime/*.dll" "$StagingDir/native/" -Force
Copy-Item "prebuilt/sympy_engine.dll" "$StagingDir/native/" -Force
Copy-Item "models/MiniCPM5-2B-Q4_K_M.gguf" "$StagingDir/models/" -Force
Copy-Item "data/knowledge_seed.db" "$StagingDir/data/" -Force
Copy-Item "install.ps1","start.bat" "$StagingDir/" -Force

# ---------- 7. 计算 manifest.sha256 ----------
$manifest = @()
Get-ChildItem "$StagingDir/models/*.gguf","$StagingDir/native/*.dll" -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    $manifest += "$hash  $($_.Name)"
}
$manifest | Set-Content "$StagingDir/manifest.sha256" -Encoding UTF8

# ---------- 8. 打包 ----------
# 使用 Zip64（Compress-Archive 在 PowerShell 7 中默认启用）
Compress-Archive -Path "$StagingDir/*" -DestinationPath "ai-runtime-windows-x64.zip" -CompressionLevel Optimal
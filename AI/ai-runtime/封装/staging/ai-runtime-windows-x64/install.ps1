$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. SHA256 校验（仅校验 GGUF 与关键 DLL，不逐文件全量校验）
$manifest = Get-Content "$root/manifest.sha256" | Where-Object { $_ -match '\S' }
foreach ($line in $manifest) {
    $parts = $line -split '\s+', 2
    $expected = $parts[0]; $file = Join-Path $root $parts[1]
    if (-not (Test-Path $file)) { throw "缺失文件: $parts[1]" }
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected) { throw "哈希不匹配: $($parts[1])" }
}

# 2. 种子数据库：若 data/knowledge.db 不存在则从种子复制
$dbPath = Join-Path $root "data/knowledge.db"
if (-not (Test-Path $dbPath)) {
    Copy-Item (Join-Path $root "data/knowledge_seed.db") $dbPath
}

# 3. 确保插件目录存在
New-Item -ItemType Directory -Force -Path (Join-Path $root "plugins") | Out-Null

Write-Host "安装校验通过。运行 start.bat 启动。"
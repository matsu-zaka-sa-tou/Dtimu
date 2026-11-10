# Make-IncrementalPackage.ps1
# 在编译机 A 上运行：清掉 publish 里非本次构建的产物，只留可增量替换到 B 的文件
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Path = "I:\XiongsNext\MyStudio\DiscViewer\MVCDtimu\bin\Release\net9.0\publish",

    # 本次构建的时间分界，早于该时间的文件视为旧文件
    [datetime]$Cutoff = '2026-11-10 09:11:00',

    # 即使在本次构建时间之后，也不保留在 A 的包里的 B 端配置
    [string[]]$ExcludeFiles = @(
        'appsettings.json',
        'appsettings.Development.json',
        'appsettings.Production.json',
        'dotnet-tools.json'
    ),

    # 需要强制保留的本次产物（即使文件名命中 ExcludeFiles 或时间戳判断有偏差）
    [string[]]$KeepFiles = @(),

    [switch]$RemoveEmptyDirs
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Path).Path
Write-Host "目录: $root"
Write-Host "分界: $Cutoff"
Write-Host "排除: $($ExcludeFiles -join ', ')"

$targets = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $f = $_
    if ($KeepFiles -contains $f.Name) { return $false }
    if ($ExcludeFiles -contains $f.Name) { return $true }   # 排除文件：无论新旧都删
    return ($f.LastWriteTime -lt $Cutoff)                    # 旧文件：删
}

if (-not $targets) { Write-Host "没有需要删除的文件。"; return }

Write-Host ""
Write-Host "将删除 $($targets.Count) 个文件："
$targets | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length).TrimStart('\')
    Write-Host ("  [{0:yyyy-MM-dd HH:mm}] {1}" -f $_.LastWriteTime, $rel)
}

if ($PSCmdlet.ShouldProcess("$($targets.Count) 个文件", "删除")) {
    $targets | Remove-Item -Force
    Write-Host "已删除。"

    if ($RemoveEmptyDirs) {
        Get-ChildItem -LiteralPath $root -Recurse -Directory |
            Sort-Object FullName -Descending |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
            ForEach-Object {
                Write-Host "删除空目录: $($_.FullName.Substring($root.Length).TrimStart('\'))"
                Remove-Item -LiteralPath $_.FullName -Force
            }
    }
}
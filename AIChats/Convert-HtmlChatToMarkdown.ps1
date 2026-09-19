<#
.SYNOPSIS
    把聊天界面导出的 HTML 片段还原成人类可读的 Markdown。

.DESCRIPTION
    聊天网站（DeepSeek 网页版等）复制出来的内容其实是带大量 <span>/<div> 的 HTML，
    只是扩展名被写成了 .md。本脚本把它还原成真正的 Markdown：

        <h1>~<h4>                   -> # / ## / ### / ####
        <div class="md-code-block"> -> 带语言标注的围栏代码块（```js / ```css）
        <table>/<tr>/<th>/<td>      -> Markdown 表格
        <ul>/<ol>/<li>              -> - / 1. 列表（支持嵌套与列表项内的代码块、表格）
        <strong>/<code>/<em>        -> **粗体** / `行内代码` / *斜体*
        <hr>                        -> ---
        装饰性 <svg>（复制按钮、角标图标）-> 丢弃

.PARAMETER Path
    输入的 HTML 片段文件。默认在脚本所在目录里自动查找第一个含
    `ds-markdown` / `md-code-block` 的 .html / .md / .txt 文件。

.PARAMETER Out
    输出的 Markdown 文件。默认与输入同名，只把扩展名换成 .md
    （例如 `...本轮对话总结.html` -> `...本轮对话总结.md`）。

.PARAMETER Encoding
    读取源文件的编码，默认 UTF8。输出统一为 UTF-8 无 BOM。

.EXAMPLE
    .\Convert-HtmlChatToMarkdown.ps1
    在当前目录下用默认文件名转换。

.EXAMPLE
    .\Convert-HtmlChatToMarkdown.ps1 -Path 'raw.md' -Out 'clean.md'
    指定输入输出。

.EXAMPLE
    .\Convert-HtmlChatToMarkdown.ps1 -Out 'D:\notes\总结.md'
    指定输出，输入自动检测。
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Path,
    [Parameter(Position = 1)][string]$Out,
    [string]$Encoding = 'UTF8'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 默认路径
if (-not $Path) {
    $cand = Get-ChildItem -LiteralPath $PSScriptRoot -File |
        Where-Object { $_.Extension -in '.html', '.htm', '.md', '.txt' } |
        Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8) -match 'ds-markdown|md-code-block' } |
        Select-Object -First 1
    if (-not $cand) { throw '未找到 HTML 片段输入文件，请用 -Path 指定。' }
    $Path = $cand.FullName
}
$Path = (Resolve-Path -LiteralPath $Path).Path

if (-not $Out) {
    $Out = [System.IO.Path]::ChangeExtension($Path, '.md')
    if ($Out -eq $Path) { $Out = $Path + '.converted.md' }
}
$Out = [System.IO.Path]::GetFullPath($Out)
if ($Out -eq $Path) { throw "输出路径不能与输入路径相同：$Path" }

# ---------------------------------------------------------------- 正则与工具
$TagRx = [regex]'<(/?)([a-zA-Z0-9]+)((?:"[^"]*"|''[^'']*''|[^>"''])*)>'
$CodeDivRx = [regex]'<div class="md-code-block[^"]*"'
$LangRx = [regex]'class="d813de27">([^<]{0,20})</span>'
$PreRx = [regex]'(?s)<pre[^>]*>(.*?)</pre>'
$TableRx = [regex]'(?s)<table[^>]*>.*?</table>'
$RowRx = [regex]'(?s)<tr[^>]*>(.*?)</tr>'
$CellRx = [regex]'(?s)<t[hd][^>]*>(.*?)</t[hd]>'
$MarkMap = @{ h1 = '# '; h2 = '## '; h3 = '### '; h4 = '#### ' }
$Transparent = @('span', 'div', 'tbody', 'thead', 'svg', 'path')   # 直接透传的容器
$PhPrefix = '@@BLK:'
$PhRx = [regex]'(@@BLK:\d+@@)'

function Get-BalancedDivEnd {
    <# 从 <div 的 Start 处开始，找到与之配对的 </div>，返回结束下标（不含）。 #>
    param([string]$Text, [int]$Start, [regex]$TagRx)

    $depth = 0
    foreach ($m in $TagRx.Matches($Text, $Start)) {
        if ($m.Groups[2].Value -ne 'div') { continue }
        if ($m.Groups[1].Value -eq '/') {
            $depth--
            if ($depth -eq 0) { return $m.Index + $m.Length }
        }
        else {
            $depth++
        }
    }
    return $Text.Length
}

function Get-TagStripped {
    <# 去掉标签并还原 HTML 实体（&lt; -> <、&gt; -> >）。 #>
    param([string]$Html, [regex]$TagRx)
    return [System.Net.WebUtility]::HtmlDecode($TagRx.Replace($Html, ''))
}

function Add-Indented {
    <# 给多行文本整体加缩进，空行不动。 #>
    param([string]$Block, [string]$Pad)
    ($Block -split "`n" | ForEach-Object { if ($_.Trim()) { $Pad + $_ } else { $_ } }) -join "`n"
}

function Add-Block {
    <# 把已成型的块挂到最近的 li 或 root 上（列表项可以容纳多个子块）。 #>
    param($Ctx, [string]$Block)

    for ($i = $Ctx.Frames.Count - 1; $i -ge 0; $i--) {
        $t = $Ctx.Frames[$i]['type']
        if ($t -eq 'li' -or $t -eq 'root') {
            [void]$Ctx.Frames[$i]['subs'].Add($Block)
            return
        }
    }
}

function Invoke-Flush {
    <# 取出文本缓冲，按占位符切成「段落」与「预置块」。 #>
    param($Ctx)

    $text = $Ctx.Subs.ToString()
    [void]$Ctx.Subs.Clear()
    if (-not $text) { return }

    foreach ($piece in [regex]::Split($text, $PhRx.ToString())) {
        if ([string]::IsNullOrEmpty($piece)) { continue }
        $m = [regex]::Match($piece, '^' + [regex]::Escape($PhPrefix) + '(\d+)@@$')
        if ($m.Success) {
            Add-Block -Ctx $Ctx -Block ($PhPrefix + $m.Groups[1].Value + '@@')
        }
        else {
            $piece = $piece.Trim()
            if ($piece) { Add-Block -Ctx $Ctx -Block $piece }
        }
    }
}

function Format-ListItem {
    <# 把列表项的多个子块拼成带正确缩进的 Markdown 片段。 #>
    param([System.Collections.Generic.List[string]]$SubBlocks, [string]$Marker)

    $pad = ' ' * $Marker.Length
    $sb = [System.Text.StringBuilder]::new()
    for ($i = 0; $i -lt $SubBlocks.Count; $i++) {
        if ($i -eq 0) {
            $lines = $SubBlocks[0] -split "`n"
            [void]$sb.Append($Marker + $lines[0])
            for ($j = 1; $j -lt $lines.Count; $j++) {
                [void]$sb.Append("`n" + $pad + $lines[$j])
            }
        }
        else {
            [void]$sb.Append("`n`n")
            [void]$sb.Append((Add-Indented -Block $SubBlocks[$i] -Pad $pad))
        }
    }
    return $sb.ToString()
}

function Set-Placeholders {
    <# 单遍替换；-Indent 为真时把行首占位符前面的空白缩进也套用到块的每一行。 #>
    param(
        [string]$Text,
        [System.Collections.Generic.List[string]]$Blocks,
        [string]$Pattern,
        [bool]$Indent
    )

    $rx = [regex]$Pattern
    $sb = [System.Text.StringBuilder]::new()
    $pos = 0
    foreach ($m in $rx.Matches($Text)) {
        [void]$sb.Append($Text.Substring($pos, $m.Index - $pos))
        $body = $Blocks[[int]$m.Groups[$m.Groups.Count - 1].Value]
        if ($Indent -and $m.Groups.Count -gt 2) {
            $pad = $m.Groups[1].Value
            if ($pad.Length -gt 0) { $body = Add-Indented -Block $body -Pad $pad }
        }
        [void]$sb.Append($body)
        $pos = $m.Index + $m.Length
    }
    [void]$sb.Append($Text.Substring($pos))
    return $sb.ToString()
}

function Expand-Placeholders {
    <# 把 @@BLK:n@@ 换成真正的代码块/表格；行首的还要补上列表缩进。 #>
    param([string]$Text, [System.Collections.Generic.List[string]]$Blocks)

    $esc = [regex]::Escape($PhPrefix)
    # 先处理行首（可能带列表缩进）的占位符
    $Text = Set-Placeholders -Text $Text -Blocks $Blocks -Indent $true `
        -Pattern ('(?m)^([ \t]*)(' + $esc + '(\d+)@@)')
    # 再兜底处理行中残留的
    if ($Text.Contains($PhPrefix)) {
        $Text = Set-Placeholders -Text $Text -Blocks $Blocks -Indent $false `
            -Pattern ($esc + '(\d+)@@')
    }
    return $Text
}

# ---------------------------------------------------------------- 读取源文件
$src = Get-Content -LiteralPath $Path -Raw -Encoding $Encoding
$src = $src -replace "`r`n", "`n"
$src = $src -replace "`r", "`n"

$blocks = [System.Collections.Generic.List[string]]::new()   # 代码块 / 表格成品

# ---------------------------------------------------------------- 1. 挖出代码块
while ($true) {
    $m = $CodeDivRx.Match($src)
    if (-not $m.Success) { break }

    $end = Get-BalancedDivEnd -Text $src -Start $m.Index -TagRx $TagRx
    $chunk = $src.Substring($m.Index, $end - $m.Index)

    $lang = ''
    $lm = $LangRx.Match($chunk)
    if ($lm.Success) { $lang = $lm.Groups[1].Value.Trim() }

    $code = ''
    $pm = $PreRx.Match($chunk)
    if ($pm.Success) { $code = (Get-TagStripped -Html $pm.Groups[1].Value -TagRx $TagRx).Trim("`n") }

    $idx = $blocks.Count
    $blocks.Add('```' + $lang + "`n" + $code + "`n" + '```')

    $src = $src.Remove($m.Index, $end - $m.Index).Insert($m.Index, ($PhPrefix + $idx + '@@'))
}

# ---------------------------------------------------------------- 2. 表格转 Markdown
while ($true) {
    $m = $TableRx.Match($src)
    if (-not $m.Success) { break }

    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($r in $RowRx.Matches($m.Value)) {
        $cells = [System.Collections.Generic.List[string]]::new()
        foreach ($c in $CellRx.Matches($r.Groups[1].Value)) {
            $cells.Add((Get-TagStripped -Html $c.Groups[1].Value -TagRx $TagRx).Trim())
        }
        $rows.Add($cells)
    }

    if ($rows.Count -gt 0) {
        $w = 0
        foreach ($r in $rows) { if ($r.Count -gt $w) { $w = $r.Count } }

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add('| ' + (($rows[0] + @('') * ($w - $rows[0].Count)) -join ' | ') + ' |')
        $lines.Add('| ' + ((1..$w | ForEach-Object { '---' }) -join ' | ') + ' |')
        for ($i = 1; $i -lt $rows.Count; $i++) {
            $lines.Add('| ' + (($rows[$i] + @('') * ($w - $rows[$i].Count)) -join ' | ') + ' |')
        }

        $idx = $blocks.Count
        $blocks.Add(($lines -join "`n"))
        $src = $src.Remove($m.Index, $m.Length).Insert($m.Index, ($PhPrefix + $idx + '@@'))
    }
    else {
        $src = $src.Remove($m.Index, $m.Length)
    }
}

# ---------------------------------------------------------------- 3. 遍历骨架
$Ctx = @{
    Subs   = [System.Text.StringBuilder]::new()
    Frames = [System.Collections.Generic.List[object]]::new()
}
$Ctx.Frames.Add(@{ type = 'root'; subs = [System.Collections.Generic.List[string]]::new() })

$pos = 0
foreach ($m in $TagRx.Matches($src)) {
    if ($m.Index -gt $pos) {
        [void]$Ctx.Subs.Append([System.Net.WebUtility]::HtmlDecode($src.Substring($pos, $m.Index - $pos)))
    }
    $pos = $m.Index + $m.Length

    $closing = $m.Groups[1].Value -eq '/'
    $name = $m.Groups[2].Value
    $attrs = $m.Groups[3].Value

    if ($Transparent -contains $name) { continue }

    if ($name -eq 'br') { [void]$Ctx.Subs.Append("`n"); continue }

    if ($name -eq 'hr') {
        Invoke-Flush $Ctx
        Add-Block -Ctx $Ctx -Block '---'
        continue
    }

    if ($MarkMap.ContainsKey($name)) {
        if ($closing) { Invoke-Flush $Ctx } else { [void]$Ctx.Subs.Append($MarkMap[$name]) }
        continue
    }

    if ($name -eq 'p') {
        if ($closing) { Invoke-Flush $Ctx }
        continue
    }

    if ($name -eq 'ul' -or $name -eq 'ol') {
        if ($closing) {
            Invoke-Flush $Ctx
            $fr = $Ctx.Frames[$Ctx.Frames.Count - 1]
            $Ctx.Frames.RemoveAt($Ctx.Frames.Count - 1)
            $items = @($fr['items'] | Where-Object { $_ -and $_.Trim() })
            if ($items.Count -gt 0) { Add-Block -Ctx $Ctx -Block ($items -join "`n") }
        }
        else {
            Invoke-Flush $Ctx
            $start = 1
            $sm = [regex]::Match($attrs, 'start="(\d+)"')
            if ($sm.Success) { $start = [int]$sm.Groups[1].Value }
            $Ctx.Frames.Add(@{
                    type  = 'list'
                    kind  = $name
                    n     = $start
                    items = [System.Collections.Generic.List[string]]::new()
                })
        }
        continue
    }

    if ($name -eq 'li') {
        if ($closing) {
            Invoke-Flush $Ctx
            $fr = $Ctx.Frames[$Ctx.Frames.Count - 1]
            $Ctx.Frames.RemoveAt($Ctx.Frames.Count - 1)
            $item = Format-ListItem -SubBlocks $fr['subs'] -Marker $fr['marker']
            if ($item.Trim()) {
                [void]$Ctx.Frames[$Ctx.Frames.Count - 1]['items'].Add($item)
            }
        }
        else {
            $lex = $Ctx.Frames[$Ctx.Frames.Count - 1]
            if ($lex['type'] -eq 'list' -and $lex['kind'] -eq 'ol') {
                $marker = '{0}. ' -f $lex['n']
                $lex['n'] = $lex['n'] + 1
            }
            else {
                $marker = '- '
            }
            $Ctx.Frames.Add(@{
                    type   = 'li'
                    subs   = [System.Collections.Generic.List[string]]::new()
                    marker = $marker
                })
        }
        continue
    }

    if ($name -eq 'strong') { [void]$Ctx.Subs.Append('**'); continue }
    if ($name -eq 'code') { [void]$Ctx.Subs.Append('`'); continue }
    if ($name -eq 'em') { [void]$Ctx.Subs.Append('*'); continue }
}

Invoke-Flush $Ctx

# ---------------------------------------------------------------- 4. 收尾
$doc = ($Ctx.Frames[0]['subs'] -join "`n`n")
$doc = [regex]::Replace($doc, '[ \t]+\n', "`n")
$doc = [regex]::Replace($doc, '\n{3,}', "`n`n")
$doc = Expand-Placeholders -Text $doc -Blocks $blocks
$doc = [regex]::Replace($doc, '\n{3,}', "`n`n")
$doc = $doc.Trim() + "`n"

if ($doc.Contains($PhPrefix)) { Write-Warning '仍有未展开的 @@BLK:n@@ 占位符，输出可能不完整。' }

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($Out, $doc, $utf8NoBom)

Write-Host ("输入 : {0}" -f $Path)
Write-Host ("输出 : {0}" -f $Out)
Write-Host ("统计 : {0} 字符 / {1} 行 / {2} 个代码块或表格" -f $doc.Length, (($doc -split "`n").Count), $blocks.Count)

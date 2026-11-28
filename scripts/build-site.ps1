# Construit le site web statique (GitHub Pages) :
#   site\*.html + site\assets      -> pages (en-tête et pied de page communs : site\_partials)
#   docs\*.md                      -> pages de documentation HTML (petit convertisseur Markdown intégré, sans dépendance)
#   .\scripts\build-site.ps1                    -> dist-site\ (ouvrez dist-site\index.html dans le navigateur)
#   .\scripts\build-site.ps1 -Output _site      -> utilisé par .github\workflows\pages.yml
# Compatible Windows PowerShell 5.1 et PowerShell 7.
param([string]$Output)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "dist-site" }
elseif (-not [System.IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $root $Output }
$site = Join-Path $root "site"
$repoUrl = "https://github.com/Spokator/SpokaPilote"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Documents publiés, dans l'ordre d'affichage. Un nouveau .md de docs\ apparaît automatiquement dans « Autres documents ».
$docGroups = [ordered]@{
    "Utiliser SpokaPilote"  = @("INTERFACE", "CONSOLE-CENTRALE", "DEPLOIEMENT-ENTREPRISE", "POLITIQUE", "PACKS-ET-IMAGES", "IMPRIMANTES-ET-PLANIFICATION", "PILOTES-GRAPHIQUES", "MODE-PORTABLE")
    "Publier et distribuer" = @("DISTRIBUTION", "SIGNATURE", "PUBLICATION-STORE")
}

# ------------------------------------------------------------------------------------------------ Markdown -> HTML
function ConvertTo-HtmlEncoded([string]$s) { $s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace('"', "&quot;") }

# Ancre d'un titre, comme GitHub : minuscules, lettres accentuées conservées, ponctuation retirée, espaces -> tirets.
function Get-Slug([string]$text) {
    $t = [System.Net.WebUtility]::HtmlDecode(($text -replace '<[^>]+>', '')).ToLowerInvariant()
    $t = [regex]::Replace($t, '[^\p{L}\p{Nd}\s_-]', '')
    return ($t.Trim() -replace '\s', '-')
}

# Lien d'un document : autre .md de docs\ -> page HTML du site ; autre fichier du dépôt -> GitHub.
function Resolve-DocLink([string]$url) {
    if ($url -match '^(https?:|mailto:|#)') { return $url }
    $path, $anchor = $url -split '#', 2
    $anchor = if ($anchor) { "#$anchor" } else { "" }
    if ($path -match '^([A-Za-z0-9_-]+)\.md$' -and (Test-Path (Join-Path $root "docs\$path"))) { return "$($Matches[1].ToLowerInvariant()).html$anchor" }
    $full = [System.IO.Path]::GetFullPath((Join-Path (Join-Path $root "docs") $path))
    $rel = $full.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    $kind = if (Test-Path $full -PathType Container) { "tree" } else { "blob" }
    return "$repoUrl/$kind/main/$rel$anchor"
}

function Convert-Inline([string]$text) {
    $slots = New-Object System.Collections.Generic.List[string]
    $keep = { param($html) $slots.Add($html); "$([char]1)$($slots.Count - 1)$([char]2)" }
    # Code `...` (protégé de toute autre transformation).
    $text = [regex]::Replace($text, '(`+)(.+?)\1', { param($m) & $keep ("<code>" + (ConvertTo-HtmlEncoded $m.Groups[2].Value.Trim()) + "</code>") })
    # Caractères échappés \| \* \_ \\ ...
    $text = [regex]::Replace($text, '\\([\\`*_{}\[\]()#+\-.!|<>])', { param($m) & $keep (ConvertTo-HtmlEncoded $m.Groups[1].Value) })
    # Liens et images (avant l'encodage, pour lire l'adresse telle quelle).
    $text = [regex]::Replace($text, '(!?)\[([^\]]+)\]\(([^)\s]+)\)', {
            param($m)
            $label = Convert-Inline $m.Groups[2].Value
            $href = ConvertTo-HtmlEncoded (Resolve-DocLink $m.Groups[3].Value)
            if ($m.Groups[1].Value) { & $keep "<img src=`"$href`" alt=`"$(ConvertTo-HtmlEncoded $m.Groups[2].Value)`" loading=`"lazy`">" }
            else {
                $ext = if ($href -match '^https?:') { ' rel="noopener"' } else { '' }
                & $keep "<a href=`"$href`"$ext>$label</a>"
            }
        })
    $text = [regex]::Replace($text, '<(https?://[^>\s]+)>', { param($m) $u = ConvertTo-HtmlEncoded $m.Groups[1].Value; & $keep "<a href=`"$u`" rel=`"noopener`">$u</a>" })
    $text = ConvertTo-HtmlEncoded $text
    $text = [regex]::Replace($text, '(?s)\*\*(.+?)\*\*', '<strong>$1</strong>')
    $text = [regex]::Replace($text, '(?s)(?<![\w*])\*(?![\s*])(.+?)(?<![\s*])\*(?![\w*])', '<em>$1</em>')
    # Restitution des parties protégées (éventuellement imbriquées).
    for ($pass = 0; $pass -lt 3 -and $text.Contains([string][char]1); $pass++) {
        $text = [regex]::Replace($text, "$([char]1)(\d+)$([char]2)", { param($m) $slots[[int]$m.Groups[1].Value] })
    }
    return $text
}

$script:reFence = '^\s*(```|~~~)\s*([\w+-]*)\s*$'
$script:reHeading = '^(#{1,6})\s+(.+?)\s*#*\s*$'
$script:reList = '^(\s*)([-*+]|\d{1,9}[.)])(\s+|$)'
$script:reRule = '^\s*([-*_])(\s*\1){2,}\s*$'
function Test-BlockStart([string]$line) {
    ($line -match $script:reFence) -or ($line -match $script:reHeading) -or ($line -match '^\s*>') -or
    ($line -match $script:reList) -or ($line -match '^\s*\|') -or ($line -match $script:reRule)
}
function Get-Indent([string]$line) { $line.Length - $line.TrimStart(' ').Length }
function Split-TableRow([string]$line) {
    $l = $line.Trim()
    if ($l.StartsWith("|")) { $l = $l.Substring(1) }
    if ($l.EndsWith("|") -and -not $l.EndsWith("\|")) { $l = $l.Substring(0, $l.Length - 1) }
    # Séparateur « | » hors code `...` et non échappé.
    $cells = New-Object System.Collections.Generic.List[string]
    $cur = New-Object System.Text.StringBuilder
    $inCode = $false
    for ($k = 0; $k -lt $l.Length; $k++) {
        $c = $l[$k]
        if ($c -eq '`') { $inCode = -not $inCode }
        if ($c -eq '\' -and $k + 1 -lt $l.Length -and $l[$k + 1] -eq '|') { [void]$cur.Append('\|'); $k++; continue }
        if ($c -eq '|' -and -not $inCode) { $cells.Add($cur.ToString().Trim()); [void]$cur.Clear(); continue }
        [void]$cur.Append($c)
    }
    $cells.Add($cur.ToString().Trim())
    return , $cells.ToArray()
}

# Convertit une suite de lignes en blocs HTML. $Tight : paragraphes sans <p> (éléments de liste compacts).
function Convert-Blocks([string[]]$lines, [switch]$Tight, $Headings) {
    $sb = New-Object System.Text.StringBuilder
    $n = $lines.Count
    $i = 0
    while ($i -lt $n) {
        $line = $lines[$i]
        if ($line.Trim() -eq "") { $i++; continue }

        if ($line -match $script:reFence) {
            $fence = $Matches[1]; $lang = $Matches[2]; $indent = Get-Indent $line
            $code = New-Object System.Collections.Generic.List[string]
            $i++
            while ($i -lt $n -and $lines[$i].Trim() -ne $fence) {
                $l = $lines[$i]; $strip = [Math]::Min($indent, (Get-Indent $l)); $code.Add($l.Substring($strip)); $i++
            }
            $i++
            $cls = if ($lang) { " class=`"language-$lang`"" } else { "" }
            [void]$sb.Append("<pre><code$cls>").Append((ConvertTo-HtmlEncoded ($code -join "`n"))).Append("</code></pre>`n")
            continue
        }
        if ($line -match $script:reHeading) {
            $level = $Matches[1].Length; $raw = $Matches[2]
            $html = Convert-Inline $raw
            $id = Get-Slug $html
            if ($Headings -ne $null) {
                $base = $id; $k = 1
                while ($Headings.Ids.Contains($id)) { $id = "$base-$k"; $k++ }
                [void]$Headings.Ids.Add($id)
                if ($level -eq 2) { $Headings.Toc.Add([pscustomobject]@{ Id = $id; Html = $html }) }
                if ($level -eq 1 -and -not $Headings.Title) { $Headings.Title = [System.Net.WebUtility]::HtmlDecode(($html -replace '<[^>]+>', '')) }
            }
            [void]$sb.Append("<h$level id=`"$id`">$html</h$level>`n")
            $i++; continue
        }
        if ($line -match $script:reRule) { [void]$sb.Append("<hr>`n"); $i++; continue }

        if ($line -match '^\s*\|' -and $i + 1 -lt $n -and $lines[$i + 1] -match '^\s*\|?\s*:?-{2,}') {
            $head = Split-TableRow $line
            $align = Split-TableRow $lines[$i + 1] | ForEach-Object {
                if ($_ -match '^:-+:$') { "center" } elseif ($_ -match '-+:$') { "right" } else { "" }
            }
            $i += 2
            [void]$sb.Append("<div class=`"table-wrap`"><table>`n<thead><tr>")
            for ($c = 0; $c -lt $head.Count; $c++) {
                $st = if (@($align)[$c]) { " style=`"text-align:$(@($align)[$c])`"" } else { "" }
                [void]$sb.Append("<th$st>$(Convert-Inline $head[$c])</th>")
            }
            [void]$sb.Append("</tr></thead>`n<tbody>`n")
            while ($i -lt $n -and $lines[$i] -match '^\s*\|') {
                $cells = Split-TableRow $lines[$i]
                [void]$sb.Append("<tr>")
                for ($c = 0; $c -lt $head.Count; $c++) {
                    $v = if ($c -lt $cells.Count) { $cells[$c] } else { "" }
                    $st = if (@($align)[$c]) { " style=`"text-align:$(@($align)[$c])`"" } else { "" }
                    [void]$sb.Append("<td$st>$(Convert-Inline $v)</td>")
                }
                [void]$sb.Append("</tr>`n"); $i++
            }
            [void]$sb.Append("</tbody></table></div>`n")
            continue
        }
        if ($line -match '^\s*>') {
            $quote = New-Object System.Collections.Generic.List[string]
            while ($i -lt $n -and $lines[$i] -match '^\s*>') { $quote.Add(($lines[$i] -replace '^\s*>\s?', '')); $i++ }
            [void]$sb.Append("<blockquote>`n").Append((Convert-Blocks $quote.ToArray() -Headings $Headings)).Append("</blockquote>`n")
            continue
        }
        if ($line -match $script:reList) {
            $first = [regex]::Match($line, $script:reList)
            $indent = $first.Groups[1].Length
            $ordered = $first.Groups[2].Value -match '^\d'
            $start = if ($ordered) { [int]($first.Groups[2].Value.TrimEnd('.', ')')) } else { 1 }
            $tag = if ($ordered) { "ol" } else { "ul" }
            $startAttr = if ($ordered -and $start -ne 1) { " start=`"$start`"" } else { "" }
            [void]$sb.Append("<$tag$startAttr>`n")
            while ($i -lt $n) {
                $mi = [regex]::Match($lines[$i], $script:reList)
                if (-not $mi.Success -or $mi.Groups[1].Length -ne $indent -or (($mi.Groups[2].Value -match '^\d') -ne $ordered)) { break }
                $contentIndent = $mi.Length
                if ($mi.Groups[3].Value -eq "") { $contentIndent += 1 }
                $item = New-Object System.Collections.Generic.List[string]
                $item.Add($lines[$i].Substring([Math]::Min($contentIndent, $lines[$i].Length)))
                $i++
                $loose = $false
                while ($i -lt $n) {
                    $l = $lines[$i]
                    if ($l.Trim() -eq "") {
                        $j = $i; while ($j -lt $n -and $lines[$j].Trim() -eq "") { $j++ }
                        if ($j -lt $n -and (Get-Indent $lines[$j]) -gt $indent) { $item.Add(""); $loose = $true; $i++; continue }
                        break
                    }
                    $lead = Get-Indent $l
                    if ($lead -gt $indent) { $item.Add($l.Substring([Math]::Min($lead, $contentIndent))); $i++; continue }
                    if (-not (Test-BlockStart $l) -and $item[$item.Count - 1].Trim() -ne "") { $item.Add($l.Trim()); $i++; continue } # suite du paragraphe
                    break
                }
                # Une ligne vide suivie seulement de sous-listes / code ne rend pas la liste « aérée ».
                $paragraphs = @($item | Where-Object { $_.Trim() -ne "" -and -not (Test-BlockStart $_) -and (Get-Indent $_) -eq 0 }).Count
                $isTight = -not $loose -or $paragraphs -le 1
                [void]$sb.Append("<li>").Append((Convert-Blocks $item.ToArray() -Tight:$isTight -Headings $Headings).TrimEnd()).Append("</li>`n")
            }
            [void]$sb.Append("</$tag>`n")
            continue
        }
        # Paragraphe : jusqu'à une ligne vide ou un autre bloc.
        $para = New-Object System.Collections.Generic.List[string]
        while ($i -lt $n -and $lines[$i].Trim() -ne "" -and ($para.Count -eq 0 -or -not (Test-BlockStart $lines[$i]))) { $para.Add($lines[$i].Trim()); $i++ }
        $html = Convert-Inline ($para -join "`n")
        if ($Tight) { [void]$sb.Append($html).Append("`n") } else { [void]$sb.Append("<p>$html</p>`n") }
    }
    return $sb.ToString()
}

function Convert-Markdown([string]$markdown) {
    $headings = [pscustomobject]@{ Ids = (New-Object System.Collections.Generic.HashSet[string]); Toc = (New-Object System.Collections.Generic.List[object]); Title = $null }
    $lines = $markdown.TrimStart([char]0xFEFF) -split "\r?\n"
    $html = Convert-Blocks $lines -Headings $headings
    # Résumé : premier paragraphe « parlant » (au moins 60 caractères, ne se terminant pas par « : »), en texte brut.
    $summary = ""
    foreach ($m in [regex]::Matches($html, '<p>(.*?)</p>', 'Singleline')) {
        $text = ([System.Net.WebUtility]::HtmlDecode(($m.Groups[1].Value -replace '<[^>]+>', '')) -replace '\s+', ' ').Trim()
        if (-not $summary) { $summary = $text }
        if ($text.Length -ge 60 -and -not $text.EndsWith(":")) { $summary = $text; break }
    }
    if ($summary.Length -gt 220) { $summary = $summary.Substring(0, $summary.LastIndexOf(' ', 217)) + "…" }
    [pscustomobject]@{ Html = $html; Title = $headings.Title; Toc = $headings.Toc; Summary = $summary }
}

# ------------------------------------------------------------------------------------------------ Assemblage du site
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $Output "docs") | Out-Null
Copy-Item (Join-Path $site "assets") (Join-Path $Output "assets") -Recurse
Copy-Item (Join-Path $site "favicon.svg") $Output
[System.IO.File]::WriteAllText((Join-Path $Output ".nojekyll"), "", $utf8)

$header = [System.IO.File]::ReadAllText((Join-Path $site "_partials\header.html"), $utf8)
$footer = [System.IO.File]::ReadAllText((Join-Path $site "_partials\footer.html"), $utf8)
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

# En-tête et pied de page communs ; {{ROOT}} = chemin relatif vers la racine du site ; page courante signalée.
function Complete-Page([string]$html, [string]$rootPrefix) {
    $page = [regex]::Match($html, '<body[^>]*data-page="([^"]+)"').Groups[1].Value
    $html = $html.Replace("<!--#header-->", $header).Replace("<!--#footer-->", $footer)
    $html = [regex]::Replace($html, '\{\{ACTIVE:([\w-]+)\}\}', { param($m) if ($m.Groups[1].Value -eq $page) { ' aria-current="page"' } else { '' } })
    return $html.Replace("{{ROOT}}", $rootPrefix).Replace("{{VERSION}}", $version).Replace("{{REPO}}", $repoUrl)
}

# Pages de documentation.
$template = [System.IO.File]::ReadAllText((Join-Path $site "_partials\doc.html"), $utf8)
$docs = @{}
foreach ($md in Get-ChildItem (Join-Path $root "docs") -Filter *.md) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($md.Name)
    $doc = Convert-Markdown ([System.IO.File]::ReadAllText($md.FullName, $utf8))
    $slug = $name.ToLowerInvariant()
    $toc = ($doc.Toc | ForEach-Object { "<li><a href=`"#$($_.Id)`">$($_.Html)</a></li>" }) -join "`n"
    $title = if ($doc.Title) { $doc.Title } else { $name }
    $page = $template.Replace("{{DOC_TITLE}}", (ConvertTo-HtmlEncoded $title)).Replace("{{DOC_SUMMARY}}", (ConvertTo-HtmlEncoded $doc.Summary)).
        Replace("{{DOC_TOC}}", $toc).Replace("{{DOC_SOURCE}}", "$repoUrl/blob/main/docs/$($md.Name)").Replace("{{DOC_CONTENT}}", $doc.Html)
    [System.IO.File]::WriteAllText((Join-Path $Output "docs\$slug.html"), (Complete-Page $page "../"), $utf8)
    $docs[$name] = [pscustomobject]@{ Slug = $slug; Title = $title; Summary = $doc.Summary }
}

# Liste des documents pour documentation.html.
$listed = @($docGroups.Values | ForEach-Object { $_ })
$groups = [ordered]@{}
foreach ($g in $docGroups.Keys) { $groups[$g] = @($docGroups[$g] | Where-Object { $docs.ContainsKey($_) }) }
$others = @($docs.Keys | Where-Object { $listed -notcontains $_ } | Sort-Object)
if ($others.Count) { $groups["Autres documents"] = $others }
$list = New-Object System.Text.StringBuilder
foreach ($g in $groups.Keys) {
    if (-not $groups[$g].Count) { continue }
    [void]$list.Append("<h2>$(ConvertTo-HtmlEncoded $g)</h2>`n<div class=`"doc-grid`">`n")
    foreach ($name in $groups[$g]) {
        $d = $docs[$name]
        [void]$list.Append("<a class=`"doc-card`" href=`"docs/$($d.Slug).html`"><h3>$(ConvertTo-HtmlEncoded $d.Title)</h3><p>$(ConvertTo-HtmlEncoded $d.Summary)</p></a>`n")
    }
    [void]$list.Append("</div>`n")
}

# Pages HTML de site\ (hors _partials).
foreach ($file in Get-ChildItem $site -Filter *.html) {
    $html = [System.IO.File]::ReadAllText($file.FullName, $utf8).Replace("<!--#docs-list-->", $list.ToString())
    # 404.html est servie à n'importe quelle profondeur : chemins absolus du site GitHub Pages.
    $prefix = if ($file.Name -eq "404.html") { "/SpokaPilote/" } else { "" }
    [System.IO.File]::WriteAllText((Join-Path $Output $file.Name), (Complete-Page $html $prefix), $utf8)
}

# Contrôle : aucun lien interne cassé.
$broken = @()
foreach ($page in Get-ChildItem $Output -Recurse -Filter *.html) {
    $text = [System.IO.File]::ReadAllText($page.FullName, $utf8)
    foreach ($m in [regex]::Matches($text, '(?:href|src)="(?!/)([^"#:]+?)(?:#[^"]*)?"')) {
        $target = [System.IO.Path]::GetFullPath((Join-Path $page.DirectoryName ([System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value))))
        if (-not (Test-Path $target)) { $broken += "$($page.Name) -> $($m.Groups[1].Value)" }
    }
}
if ($broken) { throw "Liens internes cassés :`n$($broken -join "`n")" }
Write-Host "OK -> $Output ($($docs.Count) documents, $((Get-ChildItem $Output -Recurse -Filter *.html).Count) pages)" -ForegroundColor Green

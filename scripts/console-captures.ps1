# Captures de la console web (site, documentation), prises par Microsoft Edge sans interface via le protocole de
# débogage (aucune dépendance). À lancer sur une console de démonstration (données fictives : tools\LoadTest seed),
# jamais sur une console de production (noms de postes réels).
#   .\scripts\console-captures.ps1 -Url http://localhost:8092 -Token <jeton-admin-de-la-console-de-demo>
#   -Pages : routes à capturer (« vue », « postes »... = noms des fichiers console-<nom>.png)
param(
    [Parameter(Mandatory)] [string]$Url,
    [Parameter(Mandatory)] [string]$Token,
    [string]$Output,
    [int]$Width = 1300,
    [int]$Height = 900,
    [hashtable]$Pages = [ordered]@{
        "vue-ensemble" = "#/"
        "postes"       = "#/postes"
        "materiel"     = "#/materiel"
        "deploiement"  = "#/deploiement"
        "groupes"      = "#/groupes"
    }
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "site\assets\captures" }
New-Item -ItemType Directory -Force $Output | Out-Null

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge introuvable." }

$port = 9333
$profileDir = Join-Path $env:TEMP "spokapilote-captures-edge"
Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
$proc = Start-Process $edge -PassThru -ArgumentList @("--headless=new", "--remote-debugging-port=$port", "--user-data-dir=`"$profileDir`"",
    "--window-size=$Width,$Height", "--hide-scrollbars", "--no-first-run", "about:blank")
try {
    $target = $null
    for ($i = 0; $i -lt 50 -and -not $target; $i++) {
        Start-Sleep -Milliseconds 200
        try { $target = (Invoke-RestMethod "http://127.0.0.1:$port/json" -UseBasicParsing) | Where-Object type -eq "page" | Select-Object -First 1 } catch { }
    }
    if (-not $target) { throw "Edge ne répond pas sur le port de débogage $port." }

    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.Options.KeepAliveInterval = [TimeSpan]::Zero
    $ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:id = 0
    function Send-Cdp([string]$method, $params = @{}) {
        $script:id++
        $json = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()
        $buffer = New-Object byte[] 1048576
        while ($true) {
            $ms = New-Object IO.MemoryStream
            do {
                $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).Result
                $ms.Write($buffer, 0, $r.Count)
            } while (-not $r.EndOfMessage)
            $msg = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
            if ($msg.id -eq $script:id) {
                if ($msg.error) { throw "CDP $method : $($msg.error.message)" }
                return $msg.result
            }
        }
    }

    Send-Cdp "Emulation.setDeviceMetricsOverride" @{ width = $Width; height = $Height; deviceScaleFactor = 1; mobile = $false } | Out-Null
    Send-Cdp "Page.navigate" @{ url = $Url.TrimEnd('/') + "/" } | Out-Null
    Start-Sleep -Seconds 2
    $escaped = $Token.Replace("'", "\'")
    Send-Cdp "Runtime.evaluate" @{ expression = "sessionStorage.setItem('spokapilote.adminToken','$escaped'); location.reload(); true" } | Out-Null
    Start-Sleep -Seconds 3
    foreach ($name in $Pages.Keys) {
        Send-Cdp "Page.navigate" @{ url = $Url.TrimEnd('/') + "/" + $Pages[$name] } | Out-Null
        Start-Sleep -Seconds 3
        Send-Cdp "Runtime.evaluate" @{ expression = "window.scrollTo(0,0); true" } | Out-Null
        $shot = Send-Cdp "Page.captureScreenshot" @{ format = "png" }
        $file = Join-Path $Output "console-$name.png"
        [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($shot.data))
        Write-Host "OK -> $file"
    }
    $ws.Dispose()
}
finally {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*spokapilote-captures-edge*" } | Stop-Process -Force -ErrorAction SilentlyContinue
}

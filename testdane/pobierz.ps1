# Pobiera zdjęcia testowe z Wikimedia Commons według tabel w ZRODLA.md
# (domena publiczna oraz CC BY / CC BY-SA — autorzy i licencje są w ZRODLA.md).
# Użycie:  powershell -ExecutionPolicy Bypass -File testdane\pobierz.ps1
$ErrorActionPreference = 'Stop'
$katalog = $PSScriptRoot
$naglowki = @{ 'User-Agent' = 'ZamazywanieTwarzy-testdane/1.0 (https://github.com/Albertll/ZamazywanieTwarzy)' }
$api = 'https://commons.wikimedia.org/w/api.php?action=query&format=json&prop=imageinfo&iiprop=url|size'

$sekcja = $null
$pliki = foreach ($linia in Get-Content (Join-Path $katalog 'ZRODLA.md') -Encoding UTF8) {
    if ($linia -match '^## 1\.') { $sekcja = 'wzorce' }
    elseif ($linia -match '^## 2\.') { $sekcja = 'zdjecia' }
    elseif ($linia -match '^## ') { $sekcja = $null }
    elseif ($sekcja -and $linia -match '^\| `([^`]+)`.*(https://commons\.wikimedia\.org/wiki/(File:[^ |]+))') {
        [pscustomobject]@{ Cel = Join-Path (Join-Path $katalog $sekcja) $Matches[1]; Tytul = [uri]::UnescapeDataString($Matches[3]) }
    }
}

$i = 0
foreach ($p in $pliki) {
    $i++
    if (Test-Path -LiteralPath $p.Cel) { Write-Host "[$i/$($pliki.Count)] jest: $($p.Cel)"; continue }
    $info = (Invoke-RestMethod "$api&titles=$([uri]::EscapeDataString($p.Tytul))" -Headers $naglowki).query.pages.PSObject.Properties.Value[0].imageinfo[0]
    $url = $info.url
    # oryginał, jeśli dłuższy bok ≤ 4096 px i plik ≤ 8 MB; inaczej miniatura z dłuższym bokiem 3840 px
    if ([Math]::Max($info.width, $info.height) -gt 4096 -or $info.size -gt 8MB) {
        $szer = if ($info.width -ge $info.height) { 3840 } else { [int][Math]::Round(3840 * $info.width / $info.height) }
        $url = (Invoke-RestMethod "$api&iiurlwidth=$szer&titles=$([uri]::EscapeDataString($p.Tytul))" -Headers $naglowki).query.pages.PSObject.Properties.Value[0].imageinfo[0].thumburl
    }
    New-Item -ItemType Directory -Force (Split-Path $p.Cel) | Out-Null
    Invoke-WebRequest $url -OutFile $p.Cel -Headers $naglowki -UseBasicParsing
    Write-Host "[$i/$($pliki.Count)] pobrano: $($p.Cel)"
    Start-Sleep -Milliseconds 300
}

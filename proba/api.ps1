# Pomocnik do testów API z PowerShella: . .\api.ps1 ; Api GET /api/osoby
$script:Baza = "http://127.0.0.1:5077"
$script:Sesja = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$script:Sesja.Cookies.Add((New-Object System.Net.Cookie("zt", "testtoken0123456789abcdef", "/", "127.0.0.1")))

function Api([string]$Metoda, [string]$Sciezka, $Dane = $null) {
    $p = @{ Uri = "$script:Baza$Sciezka"; Method = $Metoda; WebSession = $script:Sesja; Headers = @{ "X-Zamazywanie" = "1" } }
    if ($null -ne $Dane) { $p.Body = [Text.Encoding]::UTF8.GetBytes(($Dane | ConvertTo-Json -Depth 6)); $p.ContentType = "application/json; charset=utf-8" }
    Invoke-RestMethod @p
}

function CzekajNaZadanie {
    do { Start-Sleep -Milliseconds 700; $z = Api GET /api/zadanie } while ($z.trwa)
    $z
}

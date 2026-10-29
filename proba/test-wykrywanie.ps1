# Test: zmiana progu wykrywania z przeniesieniem decyzji (stara baza z poprzedniej wersji -> nowa wersja).
# Użycie: pwsh -File proba\test-wykrywanie.ps1 <stary.exe> <nowy.exe>
param([string]$Stary, [string]$Nowy)
$ErrorActionPreference = 'Stop'
$dane = "F:\Claude\zamazywanie-twarzy\dane-test-wykrywanie"
$zdjecia = "F:\Claude\zamazywanie-twarzy\testdane\zdjecia"
Remove-Item $dane -Recurse -Force -ErrorAction SilentlyContinue
$env:ZAMAZYWANIE_TOKEN = "testtoken0123456789abcdef"
. "$PSScriptRoot\api.ps1"
$s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$s.Cookies.Add((New-Object System.Net.Cookie("zt", "testtoken0123456789abcdef", "/", "127.0.0.1")))
function Pobierz($url) { (Invoke-WebRequest "http://127.0.0.1:5077$url" -WebSession $s).Content | ConvertFrom-Json }
function Start-Program($exe) { $p = Start-Process $exe -ArgumentList "--port","5077","--bez-przegladarki","--dane",$dane -WindowStyle Hidden -PassThru; Start-Sleep 3; CzekajNaZadanie | Out-Null; $p }
function Zdjecie($prefiks) { $l = Pobierz "/api/zdjecia?katalog=$zdjecia&podkatalogi=false"; Pobierz "/api/zdjecia/$(($l | Where-Object nazwa -like "$prefiks*").id)" }
function Twarz($z, $x) { $z.twarze | Where-Object { -not $_.reczna -and [math]::Abs($_.x - $x) -lt 30 } | Sort-Object { [math]::Abs($_.x - $x) } | Select-Object -First 1 }
function Podsumowanie { $l = Pobierz "/api/zdjecia?katalog=$zdjecia&podkatalogi=false"; "ramek: $(($l | Measure-Object zielone -Sum).Sum + ($l | Measure-Object czerwone -Sum).Sum + ($l | Measure-Object zolte -Sum).Sum) (zielone $(($l | Measure-Object zielone -Sum).Sum), czerwone $(($l | Measure-Object czerwone -Sum).Sum), żółte $(($l | Measure-Object zolte -Sum).Sum))" }
function Decyzje {
    $z05 = Zdjecie "05"; $z03 = Zdjecie "03"; $z01 = Zdjecie "01"
    $o = Twarz $z05 1864; $n = Twarz $z03 246; $b = Twarz $z01 250
    "  05 Obama z profilu: przypisana=$($o.przypisana) kolor=$($o.kolor)"
    "  03 twarz x≈246: nieTwarz=$($n.nieTwarz) kolor=$($n.kolor)"
    "  01 Biden: wymus=$($b.wymus) kolor=$($b.kolor)"
    "  01 ramki ręczne: $(@($z01.twarze | Where-Object reczna).Count)"
}

"== 1. stara wersja: ustawienia zapisane z domyślnym progiem, przetwarzanie, decyzje"
$p = Start-Program $Stary
Api POST /api/import/wzorce @{ katalog = "F:\Claude\zamazywanie-twarzy\testdane\wzorce"; zgoda = $true } | Out-Null; CzekajNaZadanie | Out-Null
$u = Api GET /api/ustawienia; Api PUT /api/ustawienia $u | Out-Null
"  zapisany próg: $((Api GET /api/ustawienia).progWykrywania)"
Api POST /api/przetworz @{ katalog = $zdjecia; podkatalogi = $false; grupy = @() } | Out-Null; CzekajNaZadanie | Out-Null
$z05 = Zdjecie "05"; $z03 = Zdjecie "03"; $z01 = Zdjecie "01"
$obama = Twarz $z05 1864
Api POST /api/twarze/przypisz @{ twarze = @($obama.id); osobaId = $obama.kandydaci[0].osobaId; wzorzec = $true } | Out-Null
Api POST /api/twarze/oznacz @{ twarze = @((Twarz $z03 246).id); nieTwarz = $true } | Out-Null
Api POST /api/twarze/oznacz @{ twarze = @((Twarz $z01 250).id); wymus = $true } | Out-Null
Api POST "/api/zdjecia/$($z01.id)/twarze" @{ x = 100; y = 100; w = 80; h = 80 } | Out-Null
"  $(Podsumowanie)"; Decyzje
Stop-Process -Id $p.Id; Start-Sleep 1

"== 2. nowa wersja: próg po migracji ustawień"
$p = Start-Program $Nowy
$u = Api GET /api/ustawienia
"  próg: $($u.progWykrywania), wersja ustawień: $($u.wersja)"
"  przed ponownym przetworzeniem: $(Podsumowanie)"
$sw = [Diagnostics.Stopwatch]::StartNew()
Api POST /api/przetworz @{ katalog = $zdjecia; podkatalogi = $false; grupy = @() } | Out-Null; $z = CzekajNaZadanie
"  Przetwórz ($([math]::Round($sw.Elapsed.TotalSeconds,1)) s): $($z.komunikat)"
"  po: $(Podsumowanie)"; Decyzje
"  wzorce z twarzy z powiązaniem: $(@((Api GET /api/osoby) | ForEach-Object { Pobierz "/api/osoby/$($_.id)/wzorce" } | Where-Object zTwarzy).Count)"
$sw.Restart()
Api POST /api/przetworz @{ katalog = $zdjecia; podkatalogi = $false; grupy = @() } | Out-Null; CzekajNaZadanie | Out-Null
"  drugie Przetwórz bez zmian ustawień: $([math]::Round($sw.Elapsed.TotalSeconds,1)) s, $(Podsumowanie)"

"== 3. świadomy wybór 0,6 zostaje po restarcie, a Przetwórz wraca do 0,6 z decyzjami"
$u.progWykrywania = 0.6; Api PUT /api/ustawienia $u | Out-Null
Stop-Process -Id $p.Id; Start-Sleep 1
$p = Start-Program $Nowy
"  próg po restarcie: $((Api GET /api/ustawienia).progWykrywania)"
Api POST /api/przetworz @{ katalog = $zdjecia; podkatalogi = $false; grupy = @() } | Out-Null; CzekajNaZadanie | Out-Null
"  po: $(Podsumowanie)"; Decyzje
Stop-Process -Id $p.Id; Start-Sleep 1
Remove-Item $dane -Recurse -Force

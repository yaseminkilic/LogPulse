<#
.SYNOPSIS
    Testleri çalıştırmadan önce kilit yapan yetim 'testhost.exe' süreçlerini temizler,
    sonra 'dotnet test' çalıştırır. Ek argümanlar olduğu gibi 'dotnet test'e iletilir.

.DESCRIPTION
    'dotnet test' her koşuda bir testhost.exe alt süreci başlatır; bu süreç
    LogPulse.Tests\bin\Debug\... altındaki DLL'leri açık tutar. Önceki bir koşu
    graceful kapanmazsa (zaman aşımı/iptal) testhost yetim kalır ve DLL'leri kilitli
    tutar → sonraki derleme MSB3026/MSB3021 ile başarısız olur. Bu betik o yetim
    süreçleri öldürerek sorunu önler.

.EXAMPLE
    .\run-tests.ps1
.EXAMPLE
    .\run-tests.ps1 --filter "FullyQualifiedName~NotificationService"
.EXAMPLE
    .\run-tests.ps1 -v n

.NOTES
    Gelişmiş param bağlama bilinçli olarak kullanılmadı: böylece '--filter' gibi
    '--' önekli token'lar yanlış yorumlanmadan doğrudan $args ile iletilir.
    Başka bir test projesi için PROJESINI ortam değişkeniyle ver: $env:TEST_PROJECT.
#>

$ErrorActionPreference = 'Stop'
# Betik nerede çağrılırsa çağrılsın repo kökünden çalış.
Set-Location -Path $PSScriptRoot

$project = if ($env:TEST_PROJECT) { $env:TEST_PROJECT } else { 'LogPulse.Tests\LogPulse.Tests.csproj' }

function Stop-OrphanTestHosts {
    $hosts = @(Get-CimInstance Win32_Process -Filter "Name='testhost.exe'" -ErrorAction SilentlyContinue)
    if ($hosts.Count -eq 0) {
        Write-Host "Temiz: asili testhost yok." -ForegroundColor DarkGray
        return
    }

    Write-Host "$($hosts.Count) adet testhost.exe bulundu, sonlandiriliyor..." -ForegroundColor Yellow
    foreach ($p in $hosts) {
        try {
            $r = Invoke-CimMethod -InputObject $p -MethodName Terminate
            $state = if ($r.ReturnValue -eq 0) { 'OK' } else { "HATA($($r.ReturnValue))" }
            Write-Host ("  PID {0,-6} {1}" -f $p.ProcessId, $state)
        }
        catch {
            Write-Warning "  PID $($p.ProcessId) sonlandirilamadi: $($_.Exception.Message)"
        }
    }
    Start-Sleep -Milliseconds 700

    $left = @(Get-CimInstance Win32_Process -Filter "Name='testhost.exe'" -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        Write-Warning "$($left.Count) testhost hala kilitli olabilir; build basarisiz olursa tekrar deneyin."
    }
}

Write-Host "== 1/2: Yetim testhost temizligi ==" -ForegroundColor Cyan
Stop-OrphanTestHosts

Write-Host "`n== 2/2: dotnet test ==" -ForegroundColor Cyan
# $args: betiğe verilen tüm ek argümanlar; olduğu gibi (splatting) iletilir.
Write-Host "> dotnet test $project --nologo $($args -join ' ')" -ForegroundColor DarkGray
& dotnet test $project --nologo @args
exit $LASTEXITCODE

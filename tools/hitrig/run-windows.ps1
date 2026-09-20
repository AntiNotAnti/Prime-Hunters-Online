param(
    [Parameter(Mandatory=$true)][string]$Runtime,
    [Parameter(Mandatory=$true)][string]$Output,
    [ValidateSet('smoke','press-age')][string]$Suite = 'smoke',
    [int]$Seconds = 30,
    [string]$Rtts = '150,250,320,400',
    [string]$Modes = 'powerbeam,missile,sniper,magmaul,shockcoil,judicator,battlehammer,voltdriver,duel',
    [ValidateSet('Samus','Sylux')][string]$Hunter = 'Samus',
    [int]$Port = 28981
)
$ErrorActionPreference = 'Stop'
$runtimePath = (Resolve-Path -LiteralPath $Runtime).Path
$outputPath = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$exe = Join-Path $runtimePath 'FruityPrime.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Publish a staged Windows runtime first.' }
# This runner owns only processes it starts. All network endpoints are loopback.
$cases = @()
if ($Suite -eq 'press-age') {
    foreach ($rtt in ($Rtts.Split(',') | ForEach-Object { [int]$_ })) { foreach ($jitter in 0,40,80) { foreach ($loss in 0,1,2,5) {
        foreach ($age in $false,$true) {
            $cases += [pscustomobject]@{Mode='sniper'; Rtt=$rtt; Jitter=$jitter; Loss=$loss; Reorder=0; Duplicate=0; PressAge=$age}
        }
    } } }
} else {
    foreach ($mode in $Modes.Split(',')) {
        foreach ($rtt in 0,400) {
            $cases += [pscustomobject]@{Mode=$mode; Rtt=$rtt; Jitter=([int]($rtt -gt 0)*80); Loss=([int]($rtt -gt 0)*5); Reorder=([int]($rtt -gt 0)*3); Duplicate=([int]($rtt -gt 0)*3); PressAge=$false}
        }
    }
}
$results = @()
$index = 0
foreach ($case in $cases) {
    $index++
    $label = '{0:D3}-{1}-r{2}-j{3}-l{4}-age{5}' -f $index,$case.Mode,$case.Rtt,$case.Jitter,$case.Loss,[int]$case.PressAge
    $outDir = Join-Path $outputPath $label
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $case | Add-Member -NotePropertyName Hunter -NotePropertyValue $Hunter -Force
    $case | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outDir 'profile.json')
    Set-Content -LiteralPath (Join-Path $runtimePath 'maprotation.txt') -Value 'MP1 SANCTORUS | Battle | 15 | 999'
    $started = [Collections.Generic.List[Diagnostics.Process]]::new()
    try {
        $serverArgs = @('-server','-port',"$Port",'-players','2','-nomaster','-noautoupdate','-debuglog')
        if ($case.PressAge) { $serverArgs += '-pressage' }
        $serverLog = Join-Path $outDir 'server.log'
        $server = Start-Process -FilePath $exe -ArgumentList $serverArgs -WorkingDirectory $runtimePath -WindowStyle Hidden -PassThru -RedirectStandardOutput $serverLog -RedirectStandardError (Join-Path $outDir 'server.err')
        $started.Add($server)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            if ($server.HasExited) { throw "Server exited ($($server.ExitCode)): $label" }
            if ([DateTime]::UtcNow -gt $deadline) { throw "Server startup timeout: $label" }
            Start-Sleep -Milliseconds 200
            $ready = (Test-Path -LiteralPath $serverLog) -and ((Get-Content -LiteralPath $serverLog -Raw) -match 'listening on UDP')
        } until ($ready)
        Start-Sleep -Milliseconds 1500
        foreach ($name in 'ALPHA','BRAVO') {
            $clientArgs = @('-netcheck','127.0.0.1','-port',"$Port",'-name',"${label}_${name}",'-hunter',$Hunter,'-seconds',"$Seconds",'-size','320x180','-hitrig',$case.Mode,'-netlag',"$($case.Rtt)",'-netjitter',"$($case.Jitter)",'-netloss',"$($case.Loss)%",'-netreorder',"$($case.Reorder)%",'-netduplicate',"$($case.Duplicate)%",'-netseed','8128','-noautoupdate','-debuglog')
            $client = Start-Process -FilePath $exe -ArgumentList $clientArgs -WorkingDirectory $runtimePath -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $outDir "$name.log") -RedirectStandardError (Join-Path $outDir "$name.err")
            $started.Add($client)
            Start-Sleep -Milliseconds 1000
        }
        $ok = $true
        foreach ($client in $started | Select-Object -Skip 1) {
            if (!$client.WaitForExit(($Seconds + 45)*1000)) { $ok = $false }
        }
        foreach ($name in 'ALPHA','BRAVO') {
            $log = Get-Content -LiteralPath (Join-Path $outDir "$name.log") -Raw
            if ($log -notmatch 'what this client saw' -or $log -notmatch '2 player\(s\) in scene') { $ok = $false }
        }
        $results += [pscustomobject]@{Label=$label; Completed=$ok; Mode=$case.Mode; Rtt=$case.Rtt; Jitter=$case.Jitter; Loss=$case.Loss; PressAge=$case.PressAge; ClientExitCodes=(($started | Select-Object -Skip 1 | ForEach-Object { $_.ExitCode }) -join ",")}
        $results | Export-Csv -LiteralPath (Join-Path $outputPath 'runs.csv') -NoTypeInformation
        foreach ($logName in @('server', "${label}_ALPHA", "${label}_BRAVO")) {
            $safeName = $logName -replace '[^a-zA-Z0-9]', '_'
            $sourceLog = Join-Path $runtimePath "netlog-$safeName.txt"
            if (Test-Path -LiteralPath $sourceLog) { Copy-Item -LiteralPath $sourceLog -Destination $outDir -Force }
        }
        Write-Output "$index/$($cases.Count) $label completed=$ok"
    } finally {
        foreach ($process in $started) { if (!$process.HasExited) { Stop-Process -Id $process.Id -Force } }
    }
}
if ($results.Completed -contains $false) { exit 1 }

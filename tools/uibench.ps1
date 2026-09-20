# What a redraw of the launcher costs, one process per resolution and scenario.
#
# One process per size *and* per scenario, and both halves of that were found
# the hard way. ForceRenderTimerTick renders every composition target the
# process has and a top level that has been closed is not necessarily off the
# compositor yet, so sizes measured together measured each other: an idle 1440p
# frame came out at 44 ms. And the scenarios contaminate each other too --
# "repaint" invalidates every control on the page, which throws away the
# compositor's recorded draw lists, and a scroll measured after one costs four
# times what the same scroll costs on its own (33 ms against 8 at 1080p).
#
#   tools\uibench.ps1                  the surface the launcher uses now
#   tools\uibench.ps1 -Slow            the headless Window it used to use
#   tools\uibench.ps1 -Screen start    another screen
#
# Take the numbers on mains power. On battery this machine throttles to about a
# third of its clock and every figure moves with it.
param(
    [string] $Screen = "settings",
    [switch] $Slow,
    [switch] $Free,
    [string] $Shot = "",
    [string] $Exe = "C:\GIT\Fruity-Prime\src\MphRead\bin\Release\net9.0\FruityPrime.dll"
)

$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
$sizes = @("1280x720", "1600x900", "1920x1080", "2560x1440", "3840x2160")
$moves = @("still", "paint", "repaint", "scroll", "pointer", "wheel")

$surface = if ($Slow) { "headless Window, as shipped" } else { "UiTopLevelImpl" }
Write-Host ""
Write-Host "  screen=$Screen  surface $surface"
Write-Host ""
Write-Host ("  window      surface     x     what   |   input   render     grab   upload |" +
    "   total     fps |  gc ms  colls | drawn  layouts")
Write-Host ("  " + ("-" * 124))

foreach ($size in $sizes) {
    foreach ($move in $moves) {
        $bench = @("$Exe", "-uibench", $Screen, "-uibenchsize", $size, "-uibenchonly", $move)
        if ($Slow) { $bench += "-uibenchslow" }
        if ($Free) { $bench += "-uibenchfree" }
        if ($Shot -and $move -eq "scroll") { $bench += @("-uibenchshot", $Shot) }
        & dotnet @bench 2>&1 |
            Where-Object { $_ -match '\b(still|paint|repaint|scroll|pointer|wheel)\b\s*\|' } |
            ForEach-Object { Write-Host $_ }
    }
}
Write-Host ""

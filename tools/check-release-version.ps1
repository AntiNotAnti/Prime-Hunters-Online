param([string]$Bash = 'C:/Program Files/Git/bin/bash.exe')
$ErrorActionPreference = 'Stop'
# Run the real pre-checkout workflow step with gh replaced by a shell function.
# No GitHub calls, tags, downloads, or publishes are performed.
$workflow = Get-Content -Raw (Join-Path $PSScriptRoot '../.github/workflows/release.yml')
$block = [regex]::Match($workflow, '(?s)      - name: resolve the tag.*?        run: \|\r?\n(.*?)\r?\n      - uses: actions/checkout').Groups[1].Value
if (!$block) { throw 'Could not find the release tag step' }
$block = ($block -split '\r?\n' | ForEach-Object { $_ -replace '^          ', '' }) -join "`n"
$mock = @'
gh() {
  if [[ "$1" != api ]]; then return 90; fi
  if [[ "$2" == -X ]]; then
    printf '%s\n' "$*" >> "$MOCK_WRITES"
  elif [[ "$2" == */git/refs/tags ]]; then
    printf 'refs/tags/%s\n' "$MOCK_LATEST"
  elif [[ "$2" == */git/ref/tags/* ]]; then
    [[ "$MOCK_EXISTS" == 1 ]]
  else
    return 91
  fi
}
'@
$cases = @(
    @{ Tag='1.2.3'; Bump='none'; Latest=''; Good=$true; Version='1.2.3'; Code='1002003'; Write=$false },
    @{ Tag='v1.0.0'; Bump='none'; Latest=''; Good=$true; Version='1.0.0'; Code='1000000'; Write=$false },
    @{ Tag=''; Bump='major'; Latest='v0.9.0'; Good=$true; Version='1.0.0'; Code='1000000'; Write=$true },
    @{ Tag=''; Bump='patch'; Latest='v0.1.999'; Good=$false; Write=$false },
    @{ Tag='v0.0.0'; Bump='none'; Latest=''; Good=$false; Write=$false },
    @{ Tag='v1.2.3-rc1'; Bump='none'; Latest=''; Good=$false; Write=$false },
    @{ Tag='v01.2.3'; Bump='none'; Latest=''; Good=$false; Write=$false },
    @{ Tag='v18446744073709551617.0.0'; Bump='none'; Latest=''; Good=$false; Write=$false }
)
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('fruity-release-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$failures = 0
try {
    $i = 0
    foreach ($case in $cases) {
        $i++
        $start = [Diagnostics.ProcessStartInfo]::new($Bash)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add('-c')
        $start.ArgumentList.Add($mock + "`n" + $block)
        $outFile = Join-Path $fixture "out$i"
        $writes = Join-Path $fixture "writes$i"
        $envs = @{ INPUT_TAG=$case.Tag; PUSHED_TAG=''; BUMP=$case.Bump; MOCK_LATEST=$case.Latest;
            MOCK_EXISTS=$(if ($case.Tag) {'1'} else {'0'}); REPO='fixture/repo'; SHA='0123456789';
            GITHUB_OUTPUT=$outFile; GITHUB_STEP_SUMMARY=(Join-Path $fixture "summary$i"); MOCK_WRITES=$writes }
        foreach ($key in $envs.Keys) { $start.Environment[$key] = $envs[$key] }
        $process = [Diagnostics.Process]::Start($start)
        $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $pass = (($process.ExitCode -eq 0) -eq $case.Good) -and ((Test-Path $writes) -eq $case.Write)
        if ($case.Good) {
            $values = Get-Content $outFile
            $pass = $pass -and ($values -contains "version=$($case.Version)") -and ($values -contains "android_version_code=$($case.Code)")
        }
        if (!$pass) { $failures++; Write-Host "FAIL $($case | ConvertTo-Json -Compress)`n$output" }
        else { Write-Host "PASS tag='$($case.Tag)' bump=$($case.Bump) latest=$($case.Latest)" }
        $process.Dispose()
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or
        !([IO.Path]::GetFileName($resolved).StartsWith('fruity-release-check-'))) { throw 'Unsafe fixture cleanup' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if ($failures) { throw "$failures release workflow checks failed" }

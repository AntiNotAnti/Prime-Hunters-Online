# Asset-free source probe for the Spire collision pose ownership invariant.
$root = Split-Path -Parent $PSScriptRoot
$process = Get-Content -Raw (Join-Path $root 'src/MphRead/Entities/Players/PlayerProcess.cs')
$draw = Get-Content -Raw (Join-Path $root 'src/MphRead/Entities/Players/PlayerDraw.cs')
$inputCode = Get-Content -Raw (Join-Path $root 'src/MphRead/Entities/Players/PlayerInput.cs')

$checks = @(
    @{ Name = 'pose follows alt animation update'; Ok = $process -match '(?s)UpdateAnimFrames\(_altModel\);\s*}\s*if \(Hunter == Hunter\.Spire && Flags2\.TestFlag\(PlayerFlags2\.AltAttack\)\)\s*{\s*UpdateSpireAltCollisionPose\(\);' },
    @{ Name = 'simulation writes both rock positions from animated nodes'; Ok = $process -match '(?s)void UpdateSpireAltCollisionPose\(\)\s*{\s*//[^\r\n]*\s*AnimateSpireAltAttack\(\);\s*_spireRockPosL = _spireAltNodes\[0\]!\.Animation\.Row3\.Xyz \+ Position;\s*_spireRockPosR = _spireAltNodes\[1\]!\.Animation\.Row3\.Xyz \+ Position;' },
    @{ Name = 'draw does not write collision positions'; Ok = $draw -notmatch '_spireRockPos[LR]\s*=' },
    @{ Name = 'attack startup initializes both positions'; Ok = $inputCode -match '(?s)_altModel\.SetAnimation\(\(int\)SpireAltAnim\.Attack, AnimFlags\.NoLoop\);\s*_soundSource\.PlaySfx\(SfxId\.SPIRE_ALT_ATTACK\);\s*_spireRockPosR = Position;\s*_spireRockPosL = Position;' }
)

foreach ($check in $checks) {
    if (-not $check.Ok) {
        Write-Error "SPIREPOSE FAIL $($check.Name)"
        exit 1
    }
    Write-Output "SPIREPOSE ok $($check.Name)"
}

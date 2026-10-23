param([string]$AssemblyPath = (Join-Path $PSScriptRoot 'dist\Nulluxion.exe'))
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$type = $assembly.GetType('Nulluxion.HardwarePower', $true)
$method = $type.GetMethod('SupportsPowerOff', [Reflection.BindingFlags]'NonPublic,Static')
$cases = @(
    @{ Name='Typical capability'; Caps='(prot(monitor)vcp(10 12 D6(01 04 05)))'; Expected=$true },
    @{ Name='Case and hex padding'; Caps='(VCP(d6(1 0004)))'; Expected=$true },
    @{ Name='Adjacent nested feature'; Caps='(vcp(60(11 12)D6(01 04)))'; Expected=$true },
    @{ Name='Whitespace'; Caps="(vcp( D6 (`t01`n04 ) ))"; Expected=$true },
    @{ Name='Only hard-off'; Caps='(vcp(D6(01 05)))'; Expected=$false },
    @{ Name='Missing on'; Caps='(vcp(D6(04)))'; Expected=$false },
    @{ Name='Missing off'; Caps='(vcp(D6(01 02 03)))'; Expected=$false },
    @{ Name='Bare power feature'; Caps='(vcp(10 D6))'; Expected=$false },
    @{ Name='Outside VCP'; Caps='(model(D6(01 04))vcp(10 12))'; Expected=$false },
    @{ Name='Unterminated VCP'; Caps='vcp(D6(01 04)'; Expected=$false },
    @{ Name='Feature token boundary'; Caps='vcp(AD6(01 04))'; Expected=$false },
    @{ Name='Empty'; Caps=''; Expected=$false },
    @{ Name='Null'; Caps=$null; Expected=$false }
)
foreach ($case in $cases) {
    $actual = $method.Invoke($null, [object[]]@($case.Caps))
    if ($actual -ne $case.Expected) { throw "Failed: $($case.Name), expected $($case.Expected), got $actual" }
}
Write-Output "$($cases.Count) hardware capability parser cases passed (no monitor writes)."

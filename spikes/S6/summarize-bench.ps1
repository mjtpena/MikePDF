param([string]$Dir = "$PSScriptRoot\out\bench", [string]$Tag = 'r1')
# Prints a markdown table of startup numbers (ms since process-start timestamp) from the bench CSVs.
function Pct($v, $q) { if ($v.Count -eq 0) { return [double]::NaN }; $s = $v | Sort-Object; $pos = $q * ($s.Count - 1); $lo = [math]::Floor($pos); $hi = [math]::Ceiling($pos); $s[$lo] + ($s[$hi] - $s[$lo]) * ($pos - $lo) }
$rows = foreach ($f in Get-ChildItem $Dir -Filter "$Tag-*.csv" | Sort-Object Name) {
    $d = Import-Csv $f.FullName
    $mode = $f.BaseName.Substring($Tag.Length + 1)
    $first = $d | ? phase -eq 'first'
    $warm = $d | ? phase -eq 'warm'
    $fr = $warm | % { [double]$_.first_render_ms }
    $ia = $warm | % { [double]$_.idle_after_render_ms }
    $mn = $warm | % { [double]$_.main_ms }
    $cpu = $warm | % { [double]$_.sys_cpu_pct }
    '| {0} | {1} | {2:N0} | {3:N0} | {4:N0} | {5:N0} | {6:N0} | {7:N0} | {8:N0} | {9:N0} |' -f $mode, $warm.Count, ([double]$first.first_render_ms), (Pct $fr .5), (Pct $fr .95), ($fr | Measure -Maximum).Maximum, ($fr | Measure -Minimum).Minimum, (Pct $ia .5), (Pct $mn .5), ($cpu | Measure -Average).Average
}
'| mode | warm n | first run: first render | warm p50 | warm p95 | warm max | warm min | warm p50 idle-after-render | warm p50 Main() entry | mean system CPU % during runs |'
'|---|---|---|---|---|---|---|---|---|---|'
$rows

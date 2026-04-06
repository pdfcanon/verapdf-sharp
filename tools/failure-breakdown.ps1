param([string]$TrxFile = 'corpus_results_phase6b.trx')
$trx = [xml](Get-Content "tests\VeraPdfSharp.Tests\TestResults\$TrxFile")
$ns = @{t='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
$results = Select-Xml -Xml $trx -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns
$sections = @{}
foreach ($r in $results) {
    $msg = $r.Node.Output.ErrorInfo.Message
    if ($msg -match 'for ([\w_-]+\\[^\\]+\\[^\\]+)\\') {
        $section = $matches[1]
    } elseif ($msg -match 'for ([\w_-]+\\[^\\]+)\\') {
        $section = $matches[1]
    } else {
        $section = "unknown"
    }
    if (-not $sections.ContainsKey($section)) { $sections[$section] = 0 }
    $sections[$section]++
}
foreach ($s in ($sections.GetEnumerator() | Sort-Object -Property Value -Descending)) {
    Write-Host "$($s.Value.ToString().PadLeft(4)): $($s.Key)"
}
Write-Host "`nTotal: $($results.Count)"

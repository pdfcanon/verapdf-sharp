param([string]$Filter = 'Transparency')
$trx = [xml](Get-Content "tests\VeraPdfSharp.Tests\TestResults\corpus_results_phase6a.trx")
$ns = @{t='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
$results = Select-Xml -Xml $trx -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns
$files = @()
foreach ($r in $results) {
    $n = $r.Node.testName
    if ($n -match $Filter) {
        # Extract from the Output/ErrorInfo/Message
        $msg = $r.Node.Output.ErrorInfo.Message
        if ($msg -match '(veraPDF[^"]+\.pdf)') {
            $fp = $matches[1]
        } elseif ($n -match '(veraPDF[^"]+\.pdf)') {
            $fp = $matches[1]
        } else {
            $fp = $n
        }
        $files += $fp
    }
}
$sorted = $files | Sort-Object -Unique
foreach ($f in $sorted) { Write-Host $f }
Write-Host "`n--- Total: $($files.Count) failures, $($sorted.Count) unique ---"

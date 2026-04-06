param([string]$OldTrx = 'corpus_results_phase6a.trx', [string]$NewTrx = 'corpus_results_phase6b.trx')
$trxDir = Join-Path $PSScriptRoot '..\tests\VeraPdfSharp.Tests\TestResults'
$old = [xml](Get-Content (Join-Path $trxDir $OldTrx))
$new = [xml](Get-Content (Join-Path $trxDir $NewTrx))
$ns = @{t='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
# Use testId (unique per test case) instead of testName (truncated/duplicated)
$oldPassed = @{}
Select-Xml -Xml $old -XPath "//t:UnitTestResult[@outcome='Passed']" -Namespace $ns | ForEach-Object { $oldPassed[$_.Node.testId] = $true }
$regressions = @(Select-Xml -Xml $new -XPath "//t:UnitTestResult[@outcome='Failed']" -Namespace $ns | Where-Object { $oldPassed.ContainsKey($_.Node.testId) })
Write-Host "Regressions: $($regressions.Count)"
foreach ($r in $regressions) {
    $msg = $r.Node.Output.ErrorInfo.Message
    if ($msg) {
        $shortMsg = if ($msg.Length -gt 180) { $msg.Substring(0, 180) } else { $msg }
        Write-Host "  $shortMsg"
    }
}

using PdfLexer;
using PdfLexer.DOM;
using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Validation;

var corpusDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1"));

// List specific files for 7.1/12
var passFiles = Directory.EnumerateFiles(corpusDir, "*.pdf", SearchOption.AllDirectories)
    .Where(f => Path.GetFileNameWithoutExtension(f).Contains("-pass-", StringComparison.OrdinalIgnoreCase))
    .ToList();

Console.WriteLine($"Checking {passFiles.Count} pass files...");

foreach (var file in passFiles)
{
    try
    {
        using var parser = PdfLexerValidationParser.FromFile(file, PDFAFlavour.PDFUA1);
        var validator = ValidatorFactory.CreateValidator(
            new[] { PDFAFlavour.PDFUA1 },
            new ValidatorOptions(LogPassedChecks: false, ShowErrorMessages: false));
        var result = validator.Validate(parser);
        if (!result.IsCompliant)
        {
            var rel = Path.GetRelativePath(corpusDir, file);
            var has712 = result.FailedChecks.Any(fc => fc.Key.Clause == "7.1" && fc.Key.TestNumber == 12);
            var has7218 = result.FailedChecks.Any(fc => fc.Key.Clause == "7.2" && fc.Key.TestNumber == 18);
            
            if (has712)
            {
                var count = result.FailedChecks.First(fc => fc.Key.Clause == "7.1" && fc.Key.TestNumber == 12).Value;
                Console.WriteLine($"7.1/12 ({count}x): {rel}");
            }
            if (has7218)
            {
                var count = result.FailedChecks.First(fc => fc.Key.Clause == "7.2" && fc.Key.TestNumber == 18).Value;
                Console.WriteLine($"7.2/18 ({count}x): {rel}");
            }
        }
    }
    catch {}
}

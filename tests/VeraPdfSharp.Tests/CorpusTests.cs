using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Validation;

namespace VeraPdfSharp.Tests;

/// <summary>
/// Corpus-based integration tests. Each flavour with a validation profile gets its own
/// test method so results can be filtered independently.
///
/// Files with -pass- in the name must validate as compliant.
/// Files with -fail- in the name must validate as non-compliant.
/// </summary>
public sealed class CorpusTests
{
    private static readonly string CorpusRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging"));

    private static IEnumerable<object[]> EnumerateCorpusFiles(string corpusDir, PDFAFlavour flavour)
    {
        var dir = Path.Combine(CorpusRoot, corpusDir);
        if (!Directory.Exists(dir))
            yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            bool? expectedPass = name.Contains("-pass-", StringComparison.OrdinalIgnoreCase) ? true
                : name.Contains("-fail-", StringComparison.OrdinalIgnoreCase) ? false
                : null;

            if (expectedPass is null)
                continue;

            var relativePath = Path.GetRelativePath(CorpusRoot, file);
            yield return [relativePath, file, expectedPass.Value, flavour];
        }
    }

    public static IEnumerable<object[]> PdfUa1CorpusFiles() => EnumerateCorpusFiles("PDF_UA-1", PDFAFlavour.PDFUA1);
    public static IEnumerable<object[]> PdfUa2CorpusFiles() => EnumerateCorpusFiles("PDF_UA-2", PDFAFlavour.PDFUA2);
    public static IEnumerable<object[]> PdfA1aCorpusFiles() => EnumerateCorpusFiles("PDF_A-1a", PDFAFlavour.PDFA1A);
    public static IEnumerable<object[]> PdfA1bCorpusFiles() => EnumerateCorpusFiles("PDF_A-1b", PDFAFlavour.PDFA1B);
    public static IEnumerable<object[]> PdfA2aCorpusFiles() => EnumerateCorpusFiles("PDF_A-2a", PDFAFlavour.PDFA2A);
    public static IEnumerable<object[]> PdfA2bCorpusFiles() => EnumerateCorpusFiles("PDF_A-2b", PDFAFlavour.PDFA2B);
    public static IEnumerable<object[]> PdfA2uCorpusFiles() => EnumerateCorpusFiles("PDF_A-2u", PDFAFlavour.PDFA2U);
    public static IEnumerable<object[]> PdfA3bCorpusFiles() => EnumerateCorpusFiles("PDF_A-3b", PDFAFlavour.PDFA3B);
    public static IEnumerable<object[]> PdfA4CorpusFiles()  => EnumerateCorpusFiles("PDF_A-4",  PDFAFlavour.PDFA4);
    public static IEnumerable<object[]> PdfA4eCorpusFiles() => EnumerateCorpusFiles("PDF_A-4e", PDFAFlavour.PDFA4E);
    public static IEnumerable<object[]> PdfA4fCorpusFiles() => EnumerateCorpusFiles("PDF_A-4f", PDFAFlavour.PDFA4F);

    [Theory]
    [MemberData(nameof(PdfUa1CorpusFiles))]
    public void PdfUA1_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfUa2CorpusFiles))]
    public void PdfUA2_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA1aCorpusFiles))]
    public void PdfA1a_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA1bCorpusFiles))]
    public void PdfA1b_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA2aCorpusFiles))]
    public void PdfA2a_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA2bCorpusFiles))]
    public void PdfA2b_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA2uCorpusFiles))]
    public void PdfA2u_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA3bCorpusFiles))]
    public void PdfA3b_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA4CorpusFiles))]
    public void PdfA4_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA4eCorpusFiles))]
    public void PdfA4e_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    [Theory]
    [MemberData(nameof(PdfA4fCorpusFiles))]
    public void PdfA4f_CorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour) =>
        RunCorpusFile(relativePath, fullPath, expectedPass, flavour);

    private static void RunCorpusFile(string relativePath, string fullPath, bool expectedPass, PDFAFlavour flavour)
    {
        bool actualPass;
        string? errorDetail = null;
        try
        {
            using var parser = PdfLexerValidationParser.FromFile(fullPath, flavour);
            var validator = ValidatorFactory.CreateValidator(flavour);
            var result = validator.Validate(parser);
            actualPass = result.IsCompliant;
            if (!actualPass)
                errorDetail = string.Join(", ", result.FailedChecks.Keys.Select(r => $"{r.Specification}/{r.Clause}/{r.TestNumber}"));
        }
        catch (Exception ex)
        {
            actualPass = false;
            errorDetail = ex.Message;
        }

        if (expectedPass)
            Assert.True(actualPass, $"Expected PASS but got FAIL for {relativePath}. Failed rules: {errorDetail}");
        else
            Assert.False(actualPass, $"Expected FAIL but got PASS for {relativePath}.");
    }
}

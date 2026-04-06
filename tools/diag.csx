using VeraPdfSharp.Core;
using VeraPdfSharp.Model;

var corpus = @"E:\dev\VeraPdfSharp\veraPDF-corpus-staging";
var file = Directory.EnumerateFiles(Path.Combine(corpus, "PDF_A-1b"), "*.pdf", SearchOption.AllDirectories).First(f => f.Contains("-pass-"));
Console.WriteLine($"Testing: {Path.GetFileName(file)}");

using var parser = PdfLexerValidationParser.FromFile(file, PDFAFlavour.PDFA1B);
var root = parser.GetRoot();
Console.WriteLine($"Root type: {root.ObjectType}");
Console.WriteLine($"Properties: {string.Join(", ", root.Properties)}");
Console.WriteLine($"containsPDFAIdentification: {root.GetPropertyValue("containsPDFAIdentification")}");
Console.WriteLine($"streamKeywordCRLFCompliant: {root.GetPropertyValue("streamKeywordCRLFCompliant")}");
Console.WriteLine($"endstreamKeywordEOLCompliant: {root.GetPropertyValue("endstreamKeywordEOLCompliant")}");

// Check metadata
var metadataLinks = root.GetLinkedObjects("metadata");
Console.WriteLine($"Metadata objects: {metadataLinks.Count}");
foreach(var m in metadataLinks)
{
    Console.WriteLine($"  Type: {m.ObjectType}, Props: {string.Join(", ", m.Properties)}");
    if (m.ObjectType == "MainXMPPackage")
        Console.WriteLine($"  containsPDFAIdentification: {m.GetPropertyValue("containsPDFAIdentification")}");
}

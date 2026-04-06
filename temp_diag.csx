using PdfLexer;
using PdfLexer.DOM;

var path = @"veraPDF-corpus-staging\PDF_A-4\6.2 Graphics\6.2.4 Colour spaces\6.2.4.3 Uncalibrated -Device colour spaces\veraPDF test suite 6-2-4-3-t01-fail-e.pdf";
using var doc = PdfDocument.Open(File.ReadAllBytes(path));
Console.WriteLine($"Pages: {doc.Pages.Count}");
foreach (var page in doc.Pages)
{
    var res = page.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
    if (res is null) { Console.WriteLine("  No resources"); continue; }
    var csDict = res.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
    Console.WriteLine($"  ColorSpace dict: {(csDict is null ? "none" : string.Join(", ", csDict.Keys.Select(k => k.Value)))}");
    var xobjs = res.GetOptionalValue<PdfDictionary>(new PdfName("XObject"));
    if (xobjs is not null)
    {
        Console.WriteLine($"  XObjects: {string.Join(", ", xobjs.Keys.Select(k => k.Value))}");
        foreach (var key in xobjs.Keys)
        {
            var xobj = xobjs.Get(key)?.Resolve();
            if (xobj is PdfStream xs)
            {
                var sub = xs.Dictionary.GetOptionalValue<PdfName>(new PdfName("Subtype"));
                Console.WriteLine($"    {key.Value}: Subtype={sub?.Value}");
                if (sub?.Value == "Form")
                {
                    var formRes = xs.Dictionary.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
                    if (formRes is not null)
                    {
                        var fcs = formRes.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                        Console.WriteLine($"      Form CS dict: {(fcs is null ? "none" : string.Join(", ", fcs.Keys.Select(k => k.Value)))}");
                    }
                    var group = xs.Dictionary.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
                    if (group is not null)
                    {
                        Console.WriteLine($"      Group: S={group.Get(new PdfName("S"))} CS={group.Get(new PdfName("CS"))}");
                    }
                }
                else if (sub?.Value == "Image")
                {
                    var imgCS = xs.Dictionary.Get(new PdfName("ColorSpace"));
                    Console.WriteLine($"      Image CS: {imgCS}");
                }
            }
        }
    }
    // Check annotations
    var annots = page.Get(new PdfName("Annots"));
    if (annots is not null) Console.WriteLine($"  Annotations present");
    // OutputIntent on page
    var oi = page.Get(new PdfName("OutputIntents"));
    Console.WriteLine($"  Page OutputIntents: {oi is not null}");
}
// Document output intents
var docOI = doc.Catalog.Get(new PdfName("OutputIntents"));
Console.WriteLine($"Doc OutputIntents: {docOI is not null}");

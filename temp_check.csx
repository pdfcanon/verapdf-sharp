using PdfLexer;
using PdfLexer.DOM;
using PdfLexer.Parsers;
var file = args[0];
using var doc = PdfDocument.Open(file);
var page = doc.Pages[0];
var resources = page.NativeObject.Get<PdfDictionary>(PdfName.Resources);
if (resources != null) {
    var colorSpace = resources.Get<PdfDictionary>(PdfName.ColorSpace);
    if (colorSpace != null) {
        foreach (var (k,v) in colorSpace) {
            var r = v.Resolve();
            if (r is PdfArray arr && arr.Count >= 2) {
                var csType = arr[0].Resolve();
                Console.WriteLine($"  CS: {k.Value} => type={csType}");
                if (csType is PdfName pn && pn.Value == "DeviceN") {
                    // arr[1] is the names array
                    var names = arr[1].Resolve() as PdfArray;
                    Console.WriteLine($"    DeviceN names count: {names?.Count}");
                }
            }
        }
    } else { Console.WriteLine("  No ColorSpace dict"); }
} else { Console.WriteLine("  No Resources dict"); }

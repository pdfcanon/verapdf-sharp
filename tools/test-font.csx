using PdfLexer;
using PdfLexer.DOM;

var doc = PdfDocument.Open("veraPDF-corpus-staging\\PDF_A-2b\\6.2 Graphics\\6.2.11 Fonts\\6.2.11.3 Composite fonts\\6.2.11.3.1 General\\veraPDF test suite 6-2-11-3-1-t01-fail-a.pdf");
foreach (var page in doc.Pages) {
    var resources = page.NativeObject.GetOptionalValue<PdfDictionary>(PdfName.Resources);
    var fonts = resources?.GetOptionalValue<PdfDictionary>(new PdfName("Font"));
    if (fonts != null) {
        foreach (var (name, val) in fonts) {
            var fontDict = val.Resolve() as PdfDictionary;
            Console.WriteLine($"Font {name}: Subtype={fontDict?.Get(PdfName.Subtype)}");
            var encoding = fontDict?.Get(PdfName.Encoding);
            Console.WriteLine($"  Encoding raw: {encoding?.GetType().Name} = {encoding}");
            var encResolved = encoding?.Resolve();
            Console.WriteLine($"  Encoding resolved: {encResolved?.GetType().Name}");
            if (encResolved is PdfStream cmapStream) {
                Console.WriteLine($"  CMap stream keys: {string.Join(", ", cmapStream.Dictionary.Keys.Select(k => k.Value))}");
                var cidSysInfo = cmapStream.Dictionary.GetOptionalValue<PdfDictionary>(PdfName.CIDSystemInfo);
                Console.WriteLine($"  CMap CIDSystemInfo: {cidSysInfo}");
                if (cidSysInfo != null) {
                    Console.WriteLine($"    Registry: {cidSysInfo.Get(PdfName.Registry)}");
                    Console.WriteLine($"    Ordering: {cidSysInfo.Get(PdfName.Ordering)}");
                }
            }
            // Check DescendantFonts
            var desc = fontDict?.Get(new PdfName("DescendantFonts"));
            if (desc?.Resolve() is PdfArray descArr && descArr.Count > 0) {
                var cidFont = descArr[0].Resolve() as PdfDictionary;
                Console.WriteLine($"  CIDFont Subtype={cidFont?.Get(PdfName.Subtype)}");
                var cidSysInfo2 = cidFont?.GetOptionalValue<PdfDictionary>(PdfName.CIDSystemInfo);
                Console.WriteLine($"  CIDFont CIDSystemInfo: {cidSysInfo2}");
                if (cidSysInfo2 != null) {
                    Console.WriteLine($"    Registry: {cidSysInfo2.Get(PdfName.Registry)}");
                    Console.WriteLine($"    Ordering: {cidSysInfo2.Get(PdfName.Ordering)}");
                    Console.WriteLine($"    Supplement: {cidSysInfo2.Get(new PdfName("Supplement"))}");
                }
            }
        }
    }
}

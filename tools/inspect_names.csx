using PdfLexer;
using PdfLexer.DOM;

var files = new[] {
    @"veraPDF-corpus-staging\PDF_A-2b\6.1 File structure\6.1.8 Name objects\veraPDF test suite 6-1-8-t01-fail-a.pdf",
    @"veraPDF-corpus-staging\PDF_A-2b\6.1 File structure\6.1.8 Name objects\veraPDF test suite 6-1-8-t01-fail-b.pdf"
};
foreach (var f in files) {
    Console.WriteLine("=== " + System.IO.Path.GetFileName(f) + " ===");
    using var doc = PdfDocument.Open(System.IO.File.ReadAllBytes(f));
    foreach (var (page, i) in doc.Pages.Select((p, i) => (p, i))) {
        var pg = page.NativeObject;
        var res = pg.GetOptionalValue<PdfDictionary>(PdfName.Resources);
        if (res == null) continue;
        // Fonts
        var fonts = res.GetOptionalValue<PdfDictionary>(PdfName.Font);
        if (fonts != null) {
            foreach (var key in fonts.Keys) {
                var fd = fonts.Get(key)?.Resolve() as PdfDictionary;
                if (fd == null) continue;
                var bf = fd.Get(PdfName.BaseFont)?.Resolve() as PdfName;
                if (bf != null) {
                    var bytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(bf.Value);
                    var hex = BitConverter.ToString(bytes).Replace("-","");
                    Console.WriteLine($"  Font {key.Value}: BaseFont={bf.Value} hex={hex} utf8valid={System.Text.Unicode.Utf8.IsValid(bytes)}");
                }
            }
        }
        // Color spaces
        var csDict = res.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
        if (csDict != null) {
            foreach (var key in csDict.Keys) {
                var cs = csDict.Get(key)?.Resolve() as PdfArray;
                if (cs == null || cs.Count < 1) continue;
                var csName = (cs[0]?.Resolve() as PdfName)?.Value;
                if (csName == "Separation" && cs.Count > 1) {
                    var cn = (cs[1]?.Resolve() as PdfName)?.Value;
                    if (cn != null) {
                        var bytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(cn);
                        var hex = BitConverter.ToString(bytes).Replace("-","");
                        Console.WriteLine($"  Separation: name={cn} hex={hex} utf8valid={System.Text.Unicode.Utf8.IsValid(bytes)}");
                    }
                }
                if (csName == "DeviceN" && cs.Count > 1 && cs[1]?.Resolve() is PdfArray nArr) {
                    foreach (var n in nArr) {
                        var cn = (n?.Resolve() as PdfName)?.Value;
                        if (cn != null) {
                            var bytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(cn);
                            Console.WriteLine($"  DeviceN: name={cn} utf8valid={System.Text.Unicode.Utf8.IsValid(bytes)}");
                        }
                    }
                }
            }
        }
    }
}

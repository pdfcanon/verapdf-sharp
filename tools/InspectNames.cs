using PdfLexer;
using PdfLexer.DOM;
using System.Text;
using System.Text.Unicode;

var iso = Encoding.GetEncoding(""ISO-8859-1"");
var files = new[] {
    (@""veraPDF-corpus-staging\PDF_A-2b\6.1 File structure\6.1.8 Name objects\veraPDF test suite 6-1-8-t01-fail-a.pdf"", ""PDFA2B""),
    (@""veraPDF-corpus-staging\PDF_A-2b\6.1 File structure\6.1.8 Name objects\veraPDF test suite 6-1-8-t01-fail-b.pdf"", ""PDFA2B""),
};
foreach (var (f, fl) in files) {
    Console.WriteLine(""=== "" + Path.GetFileName(f) + "" ==="");
    using var doc = PdfDocument.Open(File.ReadAllBytes(f));
    // Scan entire doc for all PdfName values with non-ASCII chars
    var names = new HashSet<string>();
    void ScanObj(IPdfObject? obj, int depth, HashSet<object> seen) {
        if (obj == null || depth > 10) return;
        var r = obj.Resolve();
        if (r == null || !seen.Add(r)) return;
        if (r is PdfName nm) {
            var b = iso.GetBytes(nm.Value);
            if (b.Any(x => x > 127)) names.Add(nm.Value);
        }
        if (r is PdfDictionary d) { foreach (var kv in d) { ScanObj(kv.Key, depth+1, seen); ScanObj(kv.Value, depth+1, seen); } }
        if (r is PdfArray a) { foreach (var item in a) ScanObj(item, depth+1, seen); }
        if (r is PdfStream s) { foreach (var kv in s.Dictionary) { ScanObj(kv.Key, depth+1, seen); ScanObj(kv.Value, depth+1, seen); } }
    }
    var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
    foreach (var page in doc.Pages) ScanObj(page.NativeObject, 0, seen);
    ScanObj(doc.Catalog, 0, seen);
    foreach (var n in names) {
        var b = iso.GetBytes(n);
        Console.WriteLine($""  Name: {n} hex={BitConverter.ToString(b).Replace(""-"","""")} utf8valid={Utf8.IsValid(b)}"");
    }
    if (names.Count == 0) Console.WriteLine(""  No non-ASCII names found"");
}

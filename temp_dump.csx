using PdfLexer;
using PdfLexer.DOM;

var file = @"veraPDF-corpus-staging\PDF_UA-1\7.2 Text\7.2-t17-pass-b.pdf";
using var doc = PdfDocument.Open(File.ReadAllBytes(file));
var cat = doc.Catalog;
var stRoot = cat.GetOptionalValue<PdfDictionary>(new PdfName("StructTreeRoot"));
Console.WriteLine("StructTreeRoot keys: " + string.Join(", ", stRoot.Keys.Select(k => k.Value)));

var k = stRoot.Get(new PdfName("K"));
Console.WriteLine("/K type: " + k?.GetType()?.Name);
var resolved = k?.Resolve();
Console.WriteLine("/K resolved: " + resolved?.GetType()?.Name);

void DumpStructElem(PdfDictionary elem, string indent, int depth = 0)
{
    if (depth > 10) { Console.WriteLine(indent + "...(depth limit)"); return; }
    var type = elem.Get(PdfName.TypeName)?.ToString() ?? "(no Type)";
    var s = elem.Get(new PdfName("S"))?.ToString() ?? "(no S)";
    var hasP = elem.ContainsKey(new PdfName("P"));
    var kVal = elem.Get(new PdfName("K"));
    Console.WriteLine(indent + "Type=" + type + " S=" + s + " /P=" + hasP + " /K_type=" + kVal?.GetType()?.Name + " keys=[" + string.Join(",", elem.Keys.Select(ke=>ke.Value)) + "]");
    
    if (kVal?.Resolve() is PdfArray arr)
    {
        for (int i = 0; i < arr.Count; i++)
        {
            var item = arr[i];
            var itemR = item.Resolve();
            if (itemR is PdfDictionary d)
            {
                DumpStructElem(d, indent + "  ", depth + 1);
            }
            else
            {
                Console.WriteLine(indent + "  [int] " + itemR);
            }
        }
    }
    else if (kVal?.Resolve() is PdfDictionary kd)
    {
        DumpStructElem(kd, indent + "  ", depth + 1);
    }
}

if (resolved is PdfDictionary rootChild)
{
    DumpStructElem(rootChild, "");
}
else if (resolved is PdfArray rootArr)
{
    Console.WriteLine("Root K has " + rootArr.Count + " children");
    foreach (var item in rootArr)
    {
        if (item.Resolve() is PdfDictionary d)
            DumpStructElem(d, "");
    }
}

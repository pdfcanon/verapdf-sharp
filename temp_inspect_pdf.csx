using PdfLexer;
using PdfLexer.Parsers;
using PdfLexer.DOM;
using System.Text;

var path = @"E:\dev\VeraPdfSharp\veraPDF-corpus-staging\PDF_A-4\6.2 Graphics\6.2.4 Colour spaces\6.2.4.2 ICCBased colour spaces\veraPDF test suite 6-2-4-2-t02-pass-b.pdf";
using var ctx = new ParsingContext(new ParsingOptions());
using var doc = PdfDocument.Open(path, ctx);

var pages = doc.Catalog.GetRequiredValue<PdfArray>(PdfName.Pages)
               .GetOptionalValue<PdfArray>(new PdfName("Kids"));
if (pages == null) { Console.WriteLine("No Kids found"); return; }

foreach (var pageRef in pages)
{
    var page = pageRef.Resolve() as PdfDictionary;
    if (page == null) continue;
    
    // ExtGState resources
    var resources = page.GetOptionalValue<PdfDictionary>(PdfName.Resources);
    if (resources != null)
    {
        var extGs = resources.GetOptionalValue<PdfDictionary>(new PdfName("ExtGState"));
        if (extGs != null)
        {
            Console.WriteLine("=== ExtGState ===");
            foreach (var kv in extGs)
            {
                Console.Write($"  {kv.Key}: ");
                var gsDict = kv.Value.Resolve() as PdfDictionary;
                if (gsDict != null)
                {
                    foreach (var g in gsDict)
                        Console.Write($"{g.Key}={g.Value} ");
                }
                Console.WriteLine();
            }
        }
        
        // ColorSpace resources  
        var csDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
        if (csDict != null)
        {
            Console.WriteLine("=== ColorSpace ===");
            foreach (var kv in csDict)
            {
                Console.Write($"  {kv.Key}: ");
                var arr = kv.Value.Resolve() as PdfArray;
                if (arr != null)
                {
                    Console.Write($"[");
                    foreach (var item in arr)
                        Console.Write($"{item} ");
                    Console.Write($"]");
                    if (arr.Count > 1)
                    {
                        var stream = arr[1].Resolve() as PdfStream;
                        if (stream != null)
                        {
                            var n = stream.Dictionary.Get(new PdfName("N"));
                            Console.Write($" N={n}");
                        }
                    }
                }
                Console.WriteLine();
            }
        }
    }
    
    // Content stream
    var contents = page.Get(new PdfName("Contents"));
    if (contents != null)
    {
        var resolved = contents.Resolve();
        Console.WriteLine("=== Content Stream ===");
        if (resolved is PdfStream s)
        {
            var data = s.Contents.GetDecodedData();
            var text = Encoding.UTF8.GetString(data);
            Console.WriteLine(text.Substring(0, Math.Min(text.Length, 3000)));
        }
        else if (resolved is PdfArray arr)
        {
            foreach (var item in arr)
            {
                if (item.Resolve() is PdfStream ps)
                {
                    var data = ps.Contents.GetDecodedData();
                    var text = Encoding.UTF8.GetString(data);
                    Console.WriteLine(text.Substring(0, Math.Min(text.Length, 3000)));
                }
            }
        }
    }
}

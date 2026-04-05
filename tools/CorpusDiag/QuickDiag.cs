using PdfLexer;
using PdfLexer.DOM;
using PdfLexer.Content;
using PdfLexer.Fonts;

var path = args.Length > 0 ? args[0] : @"veraPDF-corpus-staging\PDF_A-1a\6.3 Fonts\6.3.8 Unicode character maps\veraPDF test suite 6-3-8-t01-pass-e.pdf";
using var ctx = new ParsingContext();
using var doc = ctx.OpenDocument(path);
foreach (var page in doc.Pages)
{
    var pg = new PdfPage(page.NativeObject);
    var model = pg.GetContentModel();
    foreach (var node in model.AllContent)
    {
        if (node is TextContent<double> text)
        {
            foreach (var seg in text.Segments)
            {
                var fontObj = seg.GraphicsState.FontObject?.Resolve() as PdfDictionary;
                var fontName = fontObj?.GetOptionalValue<PdfName>(new PdfName("BaseFont"));
                foreach (var gs in seg.Glyphs)
                {
                    if (gs.Glyph is { } g)
                    {
                        Console.WriteLine($"Font={fontName} Name={g.Name} Char=U+{(int)g.Char:X4} Multi={g.MultiChar} Guessed={g.GuessedUnicode} Undef={g.Undefined}");
                    }
                }
            }
        }
    }
}

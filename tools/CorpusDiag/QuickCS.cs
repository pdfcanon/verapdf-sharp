// Temporary diagnostic — delete after investigation
using PdfLexer;
using PdfLexer.DOM;
using PdfLexer.Content;
using PdfLexer.Operators;

public static class QuickCSDiag
{
    public static void Run(string[] pdfPaths)
    {
        foreach (var path in pdfPaths)
        {
            Console.WriteLine($"=== {Path.GetFileName(path)} ===");
            try
            {
                using var doc = PdfDocument.Open(File.ReadAllBytes(path));

                // Document output intents
                var docOI = doc.Catalog.Get(new PdfName("OutputIntents"));
                Console.WriteLine($"  Doc OutputIntents: {docOI is not null}");
                if (docOI?.Resolve() is PdfArray oiArr)
                {
                    foreach (var oiRef in oiArr)
                    {
                        var oi = oiRef.Resolve() as PdfDictionary;
                        if (oi is null) continue;
                        var oiS = oi.Get(new PdfName("S"));
                        var oiProfile = oi.Get(new PdfName("DestOutputProfile"));
                        Console.WriteLine($"    OI: S={oiS} hasProfile={oiProfile is not null}");
                    }
                }

                foreach (var (page, i) in doc.Pages.Select((p, i) => (p, i)))
                {
                    Console.WriteLine($"  Page {i}:");
                    Console.WriteLine($"    Page keys: {string.Join(",", page.NativeObject.Keys.Select(k => k.Value))}");
                    var res = page.NativeObject.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
                    if (res is null) { Console.WriteLine("    No resources"); continue; }
                    Console.WriteLine($"    Resource keys: {string.Join(",", res.Keys.Select(k => k.Value))}");

                    var csDict = res.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                    if (csDict is not null)
                    {
                        Console.WriteLine($"    ColorSpace dict keys: {string.Join(", ", csDict.Keys.Select(k => k.Value))}");
                        foreach (var key in csDict.Keys)
                        {
                            var csVal = csDict.Get(key)?.Resolve();
                            Console.WriteLine($"      {key.Value} = {DescribeCS(csVal)}");
                        }
                    }

                    var xobjs = res.GetOptionalValue<PdfDictionary>(new PdfName("XObject"));
                    if (xobjs is not null)
                    {
                        foreach (var key in xobjs.Keys)
                        {
                            var xobj = xobjs.Get(key)?.Resolve();
                            if (xobj is PdfStream xs)
                            {
                                var sub = xs.Dictionary.GetOptionalValue<PdfName>(new PdfName("Subtype"));
                                if (sub?.Value == "Image")
                                {
                                    var imgCS = xs.Dictionary.Get(new PdfName("ColorSpace"));
                                    Console.WriteLine($"    XObj {key.Value}: Image CS={DescribeCS(imgCS?.Resolve())}");
                                }
                                else if (sub?.Value == "Form")
                                {
                                    var formRes = xs.Dictionary.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
                                    var fcs = formRes?.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                                    var group = xs.Dictionary.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
                                    Console.WriteLine($"    XObj {key.Value}: Form CS_dict={fcs is not null} Group={group?.Get(new PdfName("CS"))}");
                                    if (fcs is not null)
                                    {
                                        foreach (var fk in fcs.Keys)
                                        {
                                            Console.WriteLine($"      FormCS {fk.Value} = {DescribeCS(fcs.Get(fk)?.Resolve())}");
                                        }
                                    }
                                    // Check form XObjects
                                    var formXobjs = formRes?.GetOptionalValue<PdfDictionary>(new PdfName("XObject"));
                                    if (formXobjs is not null)
                                    {
                                        foreach (var fxk in formXobjs.Keys)
                                        {
                                            var fxobj = formXobjs.Get(fxk)?.Resolve();
                                            if (fxobj is PdfStream fxs)
                                            {
                                                var fsub = fxs.Dictionary.GetOptionalValue<PdfName>(new PdfName("Subtype"));
                                                if (fsub?.Value == "Image")
                                                {
                                                    var imgCS2 = fxs.Dictionary.Get(new PdfName("ColorSpace"));
                                                    Console.WriteLine($"      FormXObj {fxk.Value}: Image CS={DescribeCS(imgCS2?.Resolve())}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Scan content stream operators
                    var operators = new HashSet<string>();
                    try
                    {
                        using var ctx = ParsingContext.Current;
                        var scanner = new PageContentScanner(ctx, page, flattenForms: true);
                        while (scanner.Advance())
                        {
                            var op = scanner.CurrentOperator;
                            if (op is PdfOperatorType.cs or PdfOperatorType.CS or
                                PdfOperatorType.rg or PdfOperatorType.RG or
                                PdfOperatorType.g or PdfOperatorType.G or
                                PdfOperatorType.k or PdfOperatorType.K or
                                PdfOperatorType.sc or PdfOperatorType.SC or
                                PdfOperatorType.scn or PdfOperatorType.SCN)
                            {
                                var form = scanner.CurrentForm;
                                operators.Add($"{op}{(form is not null ? " (in form)" : "")}");
                            }
                        }
                    }
                    catch { }
                    Console.WriteLine($"    CS operators: {string.Join(", ", operators.OrderBy(x => x))}");

                    // Dump Pattern resources
                    var patDict = res.GetOptionalValue<PdfDictionary>(new PdfName("Pattern"));
                    if (patDict is not null)
                    {
                        foreach (var key in patDict.Keys)
                        {
                            var patVal = patDict.Get(key)?.Resolve();
                            if (patVal is PdfStream patStream)
                            {
                                var patType = patStream.Dictionary.Get(new PdfName("PatternType"));
                                var patRes = patStream.Dictionary.GetOptionalValue<PdfDictionary>(PdfName.Resources);
                                Console.WriteLine($"    Pattern {key.Value}: type={patType} hasResources={patRes is not null}");
                                if (patRes is not null)
                                {
                                    Console.WriteLine($"      Pat resource keys: {string.Join(",", patRes.Keys.Select(k => k.Value))}");
                                    var patCSDict = patRes.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                                    if (patCSDict is not null)
                                    {
                                        foreach (var pk in patCSDict.Keys)
                                            Console.WriteLine($"      PatCS {pk.Value} = {DescribeCS(patCSDict.Get(pk)?.Resolve())}");
                                    }
                                }
                                // Dump pattern content (first 300 chars)
                                try
                                {
                                    var patBytes = patStream.Contents.GetDecodedData();
                                    var patText = System.Text.Encoding.ASCII.GetString(patBytes, 0, Math.Min(300, patBytes.Length));
                                    Console.WriteLine($"      Pat content: {patText.Replace("\r\n", " ").Replace("\n", " ")}");
                                }
                                catch { Console.WriteLine($"      Pat content: <decode error>"); }
                            }
                            else if (patVal is PdfDictionary patDictVal)
                            {
                                var patType = patDictVal.Get(new PdfName("PatternType"));
                                Console.WriteLine($"    Pattern {key.Value}: dict type={patType} (shading pattern)");
                            }
                        }
                    }

                    // Dump raw content stream (first 500 chars) if no CS operators found
                    if (operators.Count == 0)
                    {
                        var contents = page.NativeObject.Get(new PdfName("Contents"));
                        if (contents is not null)
                        {
                            var resolved = contents.Resolve();
                            if (resolved is PdfStream stream)
                            {
                                using var ms = new MemoryStream();
                                stream.Contents.CopyEncodedData(ms);
                                var bytes = ms.ToArray();
                                var text = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(500, bytes.Length));
                                Console.WriteLine($"    Raw content (first 500): {text.Replace("\r\n", " ").Replace("\n", " ")}");
                            }
                            else if (resolved is PdfArray arr)
                            {
                                Console.WriteLine($"    Content streams: {arr.Count}");
                                foreach (var s in arr.Take(2))
                                {
                                    if (s.Resolve() is PdfStream ss)
                                    {
                                        using var ms2 = new MemoryStream();
                                        ss.Contents.CopyEncodedData(ms2);
                                        var bytes = ms2.ToArray();
                                        var text = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(300, bytes.Length));
                                        Console.WriteLine($"      Stream: {text.Replace("\r\n", " ").Replace("\n", " ")}");
                                    }
                                }
                            }
                        }
                    }
                    // Page output intent
                    var pageOI = page.NativeObject.Get(new PdfName("OutputIntents"));
                    Console.WriteLine($"    Page OutputIntents: {pageOI is not null}");
                    
                    // Group
                    var pageGroup = page.NativeObject.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
                    if (pageGroup is not null)
                        Console.WriteLine($"    Page Group: S={pageGroup.Get(new PdfName("S"))} CS={pageGroup.Get(new PdfName("CS"))}");

                    // Annotations
                    var annots = page.NativeObject.Get(new PdfName("Annots"));
                    if (annots is not null)
                    {
                        var annotsArr = annots.Resolve() as PdfArray;
                        Console.WriteLine($"    Annotations: {annotsArr?.Count ?? 0}");
                        if (annotsArr is not null)
                        {
                            foreach (var annotRef in annotsArr)
                            {
                                var annot = annotRef.Resolve() as PdfDictionary;
                                if (annot is null) continue;
                                var ap = annot.GetOptionalValue<PdfDictionary>(new PdfName("AP"));
                                if (ap is not null)
                                {
                                    var n = ap.Get(new PdfName("N"))?.Resolve();
                                    if (n is PdfStream apStream)
                                    {
                                        var apRes = apStream.Dictionary.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
                                        var apCS = apRes?.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                                        Console.WriteLine($"      Annot AP/N: CS_dict={apCS is not null}");
                                        if (apCS is not null)
                                        {
                                            foreach (var ak in apCS.Keys)
                                                Console.WriteLine($"        {ak.Value} = {DescribeCS(apCS.Get(ak)?.Resolve())}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.Message}");
            }
        }
    }

    static string DescribeCS(IPdfObject? obj)
    {
        if (obj is null) return "null";
        if (obj is PdfName n) return n.Value;
        if (obj is PdfArray arr && arr.Count > 0)
        {
            var type = (arr[0].Resolve() as PdfName)?.Value ?? "?";
            return type switch
            {
                "ICCBased" => "[ICCBased ...]",
                "Indexed" => $"[Indexed base={(arr.Count > 1 ? DescribeCS(arr[1].Resolve()) : "null")} ...]",
                "Separation" => $"[Separation name={(arr.Count > 1 ? arr[1].Resolve()?.ToString() : "?")} alt={(arr.Count > 2 ? DescribeCS(arr[2].Resolve()) : "null")} ...]",
                "DeviceN" => $"[DeviceN names={(arr.Count > 1 ? arr[1].Resolve()?.ToString() : "?")} alt={(arr.Count > 2 ? DescribeCS(arr[2].Resolve()) : "null")} ...]",
                _ => $"[{type} ...]"
            };
        }
        return obj.GetType().Name;
    }
}

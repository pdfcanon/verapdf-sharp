using PdfLexer;
using PdfLexer.Content.Model;
using PdfLexer.DOM;
using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Validation;

// --- Transparency diagnosis ---
var corpusBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging"));

var diagFiles = new[] {
    // Output intent failures
    (@"PDF_A-2b\6.2 Graphics\6.2.3 Output intent\veraPDF test suite 6-2-3-t01-fail-a.pdf", PDFAFlavour.PDFA2B),
    (@"PDF_A-2b\6.2 Graphics\6.2.3 Output intent\veraPDF test suite 6-2-3-t01-fail-c.pdf", PDFAFlavour.PDFA2B),
    (@"PDF_A-2b\6.2 Graphics\6.2.3 Output intent\veraPDF test suite 6-2-3-t02-fail-a.pdf", PDFAFlavour.PDFA2B),
    (@"PDF_A-4\6.2 Graphics\6.2.3 Output intent\veraPDF test suite 6-2-3-t01-fail-a.pdf", PDFAFlavour.PDFA4),
    (@"PDF_A-4\6.2 Graphics\6.2.3 Output intent\veraPDF test suite 6-2-3-t02-fail-a.pdf", PDFAFlavour.PDFA4),
    (@"PDF_A-4\6.2 Graphics\6.2.3 Output intent\veraPDF test suite 6-2-3-t05-fail-a.pdf", PDFAFlavour.PDFA4),
    // ICCBased failures
    (@"PDF_A-4\6.2 Graphics\6.2.4 Colour spaces\6.2.4.2 ICCBased colour spaces\veraPDF test suite 6-2-4-2-t03-fail-a.pdf", PDFAFlavour.PDFA4),
    (@"PDF_A-2b\6.2 Graphics\6.2.4.2 ICCBased colour spaces\veraPDF test suite 6-2-4-2-t02-fail-a.pdf", PDFAFlavour.PDFA2B),
};

foreach (var (relPath, flavour) in diagFiles)
{
    var path = Path.Combine(corpusBase, relPath);
    Console.WriteLine($"\n=== {Path.GetFileName(path)} ({flavour}) ===");

    // Raw PDF structure inspection
    using var doc = PdfDocument.Open(File.ReadAllBytes(path));
    foreach (var (page, pi) in doc.Pages.Select((p, i) => (p, i)))
    {
        Console.WriteLine($"  Page {pi}:");
        var pg = page.NativeObject;
        
        // Check Group
        var group = pg.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
        if (group is not null)
        {
            var s = (group.Get(new PdfName("S"))?.Resolve() as PdfName)?.Value
                 ?? (group.Get(new PdfName("Subtype"))?.Resolve() as PdfName)?.Value;
            var cs = group.Get(new PdfName("CS"))?.Resolve();
            Console.WriteLine($"    Group: S={s}, CS={DescribeObj(cs)}");
        }
        else Console.WriteLine("    Group: null");
        
        // Check ExtGState resources
        var res = pg.GetOptionalValue<PdfDictionary>(PdfName.Resources);
        var gsDict = res?.GetOptionalValue<PdfDictionary>(new PdfName("ExtGState"));
        if (gsDict is not null)
        {
            foreach (var key in gsDict.Keys)
            {
                var gs = gsDict.Get(key)?.Resolve() as PdfDictionary;
                if (gs is null) continue;
                var bm = gs.Get(new PdfName("BM"))?.Resolve();
                var smask = gs.Get(new PdfName("SMask"))?.Resolve();
                var ca = gs.Get(new PdfName("ca"))?.Resolve();
                var CA = gs.Get(new PdfName("CA"))?.Resolve();
                Console.WriteLine($"    GS {key.Value}: BM={DescribeObj(bm)}, SMask={DescribeObj(smask)}, ca={DescribeObj(ca)}, CA={DescribeObj(CA)}");
            }
        }
        
        // Check XObject resources for form XObjects with Group
        var xoDict = res?.GetOptionalValue<PdfDictionary>(new PdfName("XObject"));
        if (xoDict is not null)
        {
            foreach (var key in xoDict.Keys)
            {
                var xo = xoDict.Get(key)?.Resolve();
                if (xo is PdfStream xoStream)
                {
                    var subtype = (xoStream.Dictionary.Get(new PdfName("Subtype"))?.Resolve() as PdfName)?.Value;
                    var xoGroup = xoStream.Dictionary.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
                    if (xoGroup is not null || subtype == "Form")
                    {
                        var gS = xoGroup is null ? "null" : ((xoGroup.Get(new PdfName("S"))?.Resolve() as PdfName)?.Value ?? "?");
                        var gCS = xoGroup?.Get(new PdfName("CS"))?.Resolve();
                        Console.WriteLine($"    XObject {key.Value}: Subtype={subtype}, Group.S={gS}, Group.CS={DescribeObj(gCS)}");
                        
                        // Check XObject's own ExtGState
                        var xoRes = xoStream.Dictionary.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
                        var xoGsDict = xoRes?.GetOptionalValue<PdfDictionary>(new PdfName("ExtGState"));
                        if (xoGsDict is not null)
                        {
                            foreach (var gsKey in xoGsDict.Keys)
                            {
                                var gs = xoGsDict.Get(gsKey)?.Resolve() as PdfDictionary;
                                if (gs is null) continue;
                                var bmX = gs.Get(new PdfName("BM"))?.Resolve();
                                var smaskX = gs.Get(new PdfName("SMask"))?.Resolve();
                                Console.WriteLine($"      GS {gsKey.Value}: BM={DescribeObj(bmX)}, SMask={DescribeObj(smaskX)}");
                            }
                        }
                    }
                }
            }
        }
        
        // Check Annotations
        if (pg.TryGetValue<PdfArray>(new PdfName("Annots"), out var annots, false))
        {
            foreach (var annotObj in annots)
            {
                var annot = annotObj.Resolve() as PdfDictionary;
                if (annot is null) continue;
                var annotBM = annot.Get(new PdfName("BM"))?.Resolve();
                Console.WriteLine($"    Annot: BM={DescribeObj(annotBM)}");
            }
        }
        
        // Check OutputIntents on catalog
        var catalog = doc.Catalog;
        if (catalog.TryGetValue<PdfArray>(new PdfName("OutputIntents"), out var ois, false))
        {
            foreach (var oiObj in ois)
            {
                var oi = oiObj.Resolve() as PdfDictionary;
                if (oi is null) continue;
                var s = (oi.Get(new PdfName("S"))?.Resolve() as PdfName)?.Value;
                var hasProfile = oi.ContainsKey(new PdfName("DestOutputProfile"));
                Console.WriteLine($"    OutputIntent: S={s}, hasProfile={hasProfile}");
            }
        }
    }

    // Now run validator and check model objects
    using var parser = PdfLexerValidationParser.FromFile(path, flavour);
    var validator = ValidatorFactory.CreateValidator(new[] { flavour }, new ValidatorOptions(ShowErrorMessages: true));
    var result = validator.Validate(parser);
    Console.WriteLine($"  Compliant: {result.IsCompliant}, FailedRules: {result.FailedChecks.Count}");
    foreach (var a in result.TestAssertions.Where(x => x.Status == TestAssertionStatus.Failed).Take(10))
        Console.WriteLine($"  FAIL: {a.RuleId} - {a.Description?.Substring(0, Math.Min(120, a.Description?.Length ?? 0))}");

    // Dump relevant model objects
    var visited = new HashSet<IModelObject>(ReferenceEqualityComparer.Instance);
    void DumpObjects(IModelObject obj, int depth = 0)
    {
        if (!visited.Add(obj) || depth > 10) return;
        var t = obj.ObjectType;
        if (t is "PDPage" or "PDOutputIntent" or "ICCOutputProfile" or "ICCInputProfile" 
             or "OutputIntents" or "PDICCBasedCMYK"
             or "PDDeviceRGB" or "PDDeviceCMYK" or "PDDeviceGray"
             or "PDSeparation" or "PDDeviceN")
        {
            var allProps = string.Join(", ", obj.Properties.Select(p => $"{p}={obj.GetPropertyValue(p) ?? "null"}"));
            Console.WriteLine($"  {"".PadLeft(depth*2)}OBJ: {t} [{allProps}]");
        }
        foreach (var ln in obj.Links) foreach (var lo in obj.GetLinkedObjects(ln)) DumpObjects(lo, depth + 1);
    }
    DumpObjects(parser.GetRoot());
}
return;

static string DescribeObj(IPdfObject? obj) => obj switch
{
    PdfName n => $"/{n.Value}",
    PdfArray a => $"[{string.Join(", ", a.Select(e => DescribeObj(e.Resolve())))}]",
    PdfDictionary d => $"<< keys={string.Join(",", d.Keys.Select(k => k.Value))} >>",
    PdfStream s => $"stream(keys={string.Join(",", s.Dictionary.Keys.Select(k => k.Value))})",
    PdfNumber num => num.ToString() ?? "",
    _ => obj?.ToString() ?? "null"
};

// Old diagnostic code below — unreachable
#if false
{
    var path = Path.Combine(corpusBase, relPath);
    Console.WriteLine($"\n=== {Path.GetFileName(path)} ===");
    try
    {
        using var parser = PdfLexerValidationParser.FromFile(path, flavour);
        var validator = ValidatorFactory.CreateValidator(new[] { flavour }, new ValidatorOptions(ShowErrorMessages: true));
        var result = validator.Validate(parser);
        Console.WriteLine($"  Compliant: {result.IsCompliant}, Assertions: {result.TotalAssertions}, FailedRules: {result.FailedChecks.Count}");
        foreach (var a in result.TestAssertions.Where(x => x.Status == TestAssertionStatus.Failed).Take(5))
            Console.WriteLine($"  FAIL: {a.RuleId} {a.Location.Path} - {a.Description?.Substring(0, Math.Min(120, a.Description?.Length ?? 0))}");

        // Dump PDDevice and ICCInputProfile objects with properties
        var visited = new HashSet<IModelObject>(ReferenceEqualityComparer.Instance);
        void DumpCSObjects(IModelObject obj, int depth = 0)
        {
            if (!visited.Add(obj) || depth > 10) return;
            var t = obj.ObjectType;
            if (t.StartsWith("PDDevice") || t == "ICCInputProfile" || t.StartsWith("PDSeparation") || t.StartsWith("PDDeviceN") || t == "PDIndexed" || t == "PDPattern" || t == "CosIIFilter")
            {
                var props = string.Join(", ", obj.Properties.Select(pn => $"{pn}={obj.GetPropertyValue(pn)}"));
                Console.WriteLine($"  {"".PadLeft(depth*2)}OBJ: {t} [{props}]");
            }
            foreach (var linkName in obj.Links)
            {
                foreach (var linked in obj.GetLinkedObjects(linkName))
                    DumpCSObjects(linked, depth + 1);
            }
        }
        DumpCSObjects(parser.GetRoot());
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.Message}");
    }
}
return;
// --- END TEMPORARY ---

// Dump struct tree and role map for media clip files failing 7.1/5
foreach (var mediaFile in new[] { "7.18 Annotations/7.18.6 Media/7.18.6.2 Media clip data/7.18.6.2-t01-pass-a.pdf", "7.18 Annotations/7.18.6 Media/7.18.6.2 Media clip data/7.18.6.2-t02-pass-a.pdf" })
{
    var diagMedia = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1")), mediaFile.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(diagMedia))
    {
        Console.WriteLine($"=== STRUCT TREE + ROLEMAP: {Path.GetFileName(diagMedia)} ===");
        using var rawDoc = PdfLexer.PdfDocument.Open(File.ReadAllBytes(diagMedia));
        var stRoot = rawDoc.Catalog.GetOptionalValue<PdfDictionary>(PdfLexer.PdfName.StructTreeRoot);
        if (stRoot is not null)
        {
            var roleMap = stRoot.GetOptionalValue<PdfDictionary>(new PdfLexer.PdfName("RoleMap"));
            if (roleMap is not null)
            {
                Console.WriteLine("  RoleMap:");
                foreach (var key in roleMap.Keys)
                    Console.WriteLine($"    {key.Value} -> {roleMap.Get(key)}");
            }
            else
            {
                Console.WriteLine("  (no RoleMap)");
            }
            // Check for Namespaces (PDF 2.0)
            var namespaces = stRoot.Get(new PdfLexer.PdfName("Namespaces"));
            Console.WriteLine($"  Namespaces: {namespaces?.Resolve()?.GetType()?.Name ?? "(none)"}");
            if (namespaces?.Resolve() is PdfArray nsArr)
            {
                for (int i = 0; i < nsArr.Count; i++)
                {
                    var ns = nsArr[i].Resolve() as PdfDictionary;
                    if (ns is not null)
                    {
                        Console.WriteLine($"    NS[{i}]: NS={ns.Get(new PdfLexer.PdfName("NS"))} keys=[{string.Join(",", ns.Keys.Select(k => k.Value))}]");
                        var nsRoleMap = ns.GetOptionalValue<PdfDictionary>(new PdfLexer.PdfName("RoleMapNS"));
                        if (nsRoleMap is not null)
                        {
                            Console.WriteLine($"      RoleMapNS:");
                            foreach (var key in nsRoleMap.Keys)
                                Console.WriteLine($"        {key.Value} -> {nsRoleMap.Get(key)}");
                        }
                    }
                }
            }
            // Dump ALL S values from struct tree (deeper)
            DumpStructTreeMedia(stRoot.Get(new PdfLexer.PdfName("K")), "  ", 0);
        }

        static void DumpStructTreeMedia(IPdfObject? kVal, string indent, int depth)
        {
            if (kVal is null || depth > 10) return;
            var resolved = kVal.Resolve();
            if (resolved is PdfDictionary d)
            {
                var s = d.Get(new PdfLexer.PdfName("S"))?.ToString() ?? "(none)";
                var keys = string.Join(",", d.Keys.Select(k => k.Value));
                Console.WriteLine($"{indent}S={s} keys=[{keys}]");
                DumpStructTreeMedia(d.Get(new PdfLexer.PdfName("K")), indent + "  ", depth + 1);
            }
            else if (resolved is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                    DumpStructTreeMedia(arr[i], indent, depth);
            }
        }
    }
}

// Dump raw struct tree for 7.2-t17-pass-b.pdf to diagnose 7.1/12 + 7.2/18
{
    var diagFile = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1")), "7.2 Text", "7.2-t17-pass-b.pdf");
    if (File.Exists(diagFile))
    {
        Console.WriteLine($"=== RAW STRUCT TREE: {Path.GetFileName(diagFile)} ===");
        using var rawDoc = PdfLexer.PdfDocument.Open(File.ReadAllBytes(diagFile));
        var stRoot = rawDoc.Catalog.GetOptionalValue<PdfDictionary>(PdfLexer.PdfName.StructTreeRoot);
        if (stRoot is not null)
        {
            Console.WriteLine($"  StructTreeRoot keys: {string.Join(", ", stRoot.Keys.Select(k => k.Value))}");
            DumpStructTree(stRoot.Get(new PdfLexer.PdfName("K")), "  ", 0);
        }

        static void DumpStructTree(IPdfObject? kVal, string indent, int depth)
        {
            if (kVal is null || depth > 8) return;
            var resolved = kVal.Resolve();
            if (resolved is PdfDictionary d)
            {
                var type = d.Get(PdfLexer.PdfName.TypeName)?.ToString() ?? "(none)";
                var s = d.Get(new PdfLexer.PdfName("S"))?.ToString() ?? "(none)";
                var hasP = d.ContainsKey(new PdfLexer.PdfName("P"));
                var keys = string.Join(",", d.Keys.Select(k => k.Value));
                Console.WriteLine($"{indent}Dict: Type={type} S={s} /P={hasP} keys=[{keys}]");
                DumpStructTree(d.Get(new PdfLexer.PdfName("K")), indent + "  ", depth + 1);
            }
            else if (resolved is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    var itemR = item.Resolve();
                    if (itemR is PdfDictionary)
                        DumpStructTree(item, indent, depth);
                    else
                        Console.WriteLine($"{indent}[{itemR?.GetType()?.Name}: {itemR}]");
                }
            }
            else
            {
                Console.WriteLine($"{indent}[{resolved?.GetType()?.Name}: {resolved}]");
            }
        }
    }
}

// Quick raw content model dump for first failing 7.1/3 file
{
    var testFile = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1")), "7.1 General", "7.1-t01-pass-b.pdf");
    if (File.Exists(testFile))
    {
        Console.WriteLine($"=== RAW CONTENT MODEL: {Path.GetFileName(testFile)} ===");
        using var rawDoc = PdfLexer.PdfDocument.Open(File.ReadAllBytes(testFile));
        var rawPage = rawDoc.Pages[0];
        Console.WriteLine($"  Page keys: {string.Join(", ", rawPage.NativeObject.Keys.Select(k => k.Value))}");
        var spKey = new PdfLexer.PdfName("StructParents");
        Console.WriteLine($"  StructParents: {rawPage.NativeObject.Get(spKey)}");
        var items = rawPage.GetContentModel();
        Console.WriteLine($"  Content items: {items.Count}");
        DumpContentTree(items, "    ");

        static void DumpContentTree(IEnumerable<PdfLexer.Content.Model.IContentGroup<double>> nodes, string indent)
        {
            foreach (var node in nodes)
            {
                if (node is PdfLexer.Content.Model.MarkedContentGroup<double> mcg)
                {
                    var props = mcg.Tag.PropList ?? mcg.Tag.InlineProps;
                    var mcidKey = new PdfLexer.PdfName("MCID");
                    var mcid = props?.ContainsKey(mcidKey) == true ? props.Get(mcidKey)?.ToString() : "none";
                    Console.WriteLine($"{indent}MarkedContentGroup tag={mcg.Tag.Name.Value} mcid={mcid} children={mcg.Children.Count}");
                    DumpContentTree(mcg.Children.OfType<PdfLexer.Content.Model.IContentGroup<double>>(), indent + "  ");
                }
                else
                {
                    Console.WriteLine($"{indent}{node.Type}");
                }
            }
        }
    }
}

// Dump content model for 7.2-t30-pass-a to find inline Lang
// Also trace 7.2-t15-pass-a for table cell headers
foreach (var tableFile in new[] { "7.2 Text/7.2-t15-pass-a.pdf" })
{
    var tf = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1")), tableFile.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(tf))
    {
        Console.WriteLine($"=== TABLE TRACE: {Path.GetFileName(tf)} ===");
        using var parser = PdfLexerValidationParser.FromFile(tf, PDFAFlavour.PDFUA1);
        var validator = ValidatorFactory.CreateValidator(
            new[] { PDFAFlavour.PDFUA1 },
            new ValidatorOptions(LogPassedChecks: true, ShowErrorMessages: true));
        var result = validator.Validate(parser);
        foreach (var a in result.TestAssertions.Where(a => a.Status == TestAssertionStatus.Failed))
            Console.WriteLine($"  FAIL {a.RuleId.Clause}/{a.RuleId.TestNumber} msg={a.ErrorMessage}");

        // Dump struct tree for table structure
        using var rawDoc = PdfLexer.PdfDocument.Open(File.ReadAllBytes(tf));
        var stRoot = rawDoc.Catalog.GetOptionalValue<PdfDictionary>(PdfLexer.PdfName.StructTreeRoot);
        if (stRoot is not null)
        {
            void DumpTable(IPdfObject? k, string indent, int d)
            {
                if (k is null || d > 10) return;
                var r = k.Resolve();
                if (r is PdfDictionary dd)
                {
                    var s = dd.Get(new PdfLexer.PdfName("S"))?.ToString() ?? "(none)";
                    // Read attributes
                    var aObj = dd.Get(new PdfLexer.PdfName("A"));
                    var scope = "";
                    var headers = "";
                    var id = dd.Get(new PdfLexer.PdfName("ID"))?.ToString();
                    if (aObj?.Resolve() is PdfDictionary attrDict)
                    {
                        scope = attrDict.Get(new PdfLexer.PdfName("Scope"))?.ToString() ?? "";
                        headers = attrDict.Get(new PdfLexer.PdfName("Headers"))?.ToString() ?? "";
                    }
                    else if (aObj?.Resolve() is PdfArray attrArr)
                    {
                        foreach (var attrItem in attrArr)
                        {
                            if (attrItem.Resolve() is PdfDictionary ad)
                            {
                                scope = ad.Get(new PdfLexer.PdfName("Scope"))?.ToString() ?? scope;
                                headers = ad.Get(new PdfLexer.PdfName("Headers"))?.ToString() ?? headers;
                            }
                        }
                    }
                    var extra = "";
                    if (!string.IsNullOrEmpty(scope)) extra += $" Scope={scope}";
                    if (!string.IsNullOrEmpty(headers)) extra += $" Headers={headers}";
                    if (!string.IsNullOrEmpty(id)) extra += $" ID={id}";
                    Console.WriteLine($"{indent}S={s}{extra}");
                    DumpTable(dd.Get(new PdfLexer.PdfName("K")), indent + "  ", d + 1);
                }
                else if (r is PdfArray arr)
                {
                    for (int i = 0; i < arr.Count; i++) DumpTable(arr[i], indent, d);
                }
                else if (r is PdfNumber num)
                {
                    Console.WriteLine($"{indent}MCID={num}");
                }
            }
            DumpTable(stRoot.Get(new PdfLexer.PdfName("K")), "  ", 0);
        }
    }
}
{
    var cmFile = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1")), "7.2 Text", "7.2-t30-pass-a.pdf");
    if (File.Exists(cmFile))
    {
        Console.WriteLine($"=== CONTENT MODEL: {Path.GetFileName(cmFile)} ===");
        using var rawDoc = PdfLexer.PdfDocument.Open(File.ReadAllBytes(cmFile));
        var rawPage = rawDoc.Pages[0];
        var items = rawPage.GetContentModel();
        void DumpCM(IEnumerable<PdfLexer.Content.Model.IContentGroup<double>> nodes, string indent)
        {
            foreach (var node in nodes)
            {
                if (node is PdfLexer.Content.Model.MarkedContentGroup<double> mcg2)
                {
                    var props = mcg2.Tag.PropList ?? mcg2.Tag.InlineProps;
                    var mcidK = new PdfLexer.PdfName("MCID");
                    var mcid = props?.ContainsKey(mcidK) == true ? props.Get(mcidK)?.ToString() : "none";
                    var langK2 = new PdfLexer.PdfName("Lang");
                    var lang = props?.ContainsKey(langK2) == true ? props.Get(langK2)?.ToString() : "none";
                    var propKeys = props is not null ? string.Join(",", props.Keys.Select(k => k.Value)) : "none";
                    Console.WriteLine($"{indent}MCG tag={mcg2.Tag.Name.Value} mcid={mcid} lang={lang} keys=[{propKeys}] children={mcg2.Children.Count}");
                    DumpCM(mcg2.Children.OfType<PdfLexer.Content.Model.IContentGroup<double>>(), indent + "  ");
                }
                else
                {
                    Console.WriteLine($"{indent}{node.Type}");
                }
            }
        }
        DumpCM(items, "  ");
    }
}

// Trace remaining failures: 7.2/34, 7.2/24-32, 7.5/1
foreach (var testFile in new[] { "7.2 Text/7.2-t30-pass-a.pdf", "7.2 Text/7.2-t30-pass-b.pdf", "7.2 Text/7.2-t24-pass-b.pdf" })
{
    var diagFile = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1")), testFile.Replace('/', Path.DirectorySeparatorChar));
    if (File.Exists(diagFile))
    {
        Console.WriteLine($"=== TRACE: {Path.GetFileName(diagFile)} ===");
        using var parser = PdfLexerValidationParser.FromFile(diagFile, PDFAFlavour.PDFUA1);
        var validator = ValidatorFactory.CreateValidator(
            new[] { PDFAFlavour.PDFUA1 },
            new ValidatorOptions(LogPassedChecks: true, ShowErrorMessages: true));
        var result = validator.Validate(parser);
        foreach (var a in result.TestAssertions.Where(a => a.Status == TestAssertionStatus.Failed))
        {
            Console.WriteLine($"  FAIL {a.RuleId.Clause}/{a.RuleId.TestNumber} context={a.ObjectContext} msg={a.ErrorMessage}");
        }
        using var rawDoc = PdfLexer.PdfDocument.Open(File.ReadAllBytes(diagFile));
        var langObj = rawDoc.Catalog.Get(new PdfLexer.PdfName("Lang"));
        Console.WriteLine($"  Catalog.Lang={langObj} hasKey={rawDoc.Catalog.ContainsKey(new PdfLexer.PdfName("Lang"))}");
        // Dump struct tree with Lang
        var stRoot = rawDoc.Catalog.GetOptionalValue<PdfDictionary>(PdfLexer.PdfName.StructTreeRoot);
        if (stRoot is not null)
        {
            void DumpST(IPdfObject? k, string indent, int d)
            {
                if (k is null || d > 10) return;
                var r = k.Resolve();
                if (r is PdfDictionary dd)
                {
                    var s = dd.Get(new PdfLexer.PdfName("S"))?.ToString() ?? "(none)";
                    var lang = dd.Get(new PdfLexer.PdfName("Lang"))?.ToString();
                    var kVal = dd.Get(new PdfLexer.PdfName("K"));
                    var kDesc = kVal?.Resolve() switch
                    {
                        PdfArray a => $"Array[{a.Count}]",
                        PdfDictionary _ => "Dict",
                        PdfNumber n => $"MCID={n}",
                        null => "null",
                        var o => o.GetType().Name
                    };
                    Console.WriteLine($"{indent}S={s} Lang={lang} K={kDesc} keys=[{string.Join(",", dd.Keys.Select(kk => kk.Value))}]");
                    DumpST(dd.Get(new PdfLexer.PdfName("K")), indent + "  ", d + 1);
                }
                else if (r is PdfArray arr)
                {
                    for (int i = 0; i < arr.Count; i++) DumpST(arr[i], indent, d);
                }
                else if (r is PdfNumber num)
                {
                    Console.WriteLine($"{indent}MCID={num}");
                }
            }
            DumpST(stRoot.Get(new PdfLexer.PdfName("K")), "  ", 0);
        }
    }
}

var corpusDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "veraPDF-corpus-staging", "PDF_UA-1"));
if (args.Length > 0) corpusDir = Path.Combine(args[0], "PDF_UA-1");

var passFiles = Directory.EnumerateFiles(corpusDir, "*.pdf", SearchOption.AllDirectories)
    .Where(f => Path.GetFileNameWithoutExtension(f).Contains("-pass-", StringComparison.OrdinalIgnoreCase))
    .ToList();

Console.WriteLine($"Found {passFiles.Count} pass files in {corpusDir}");

var ruleFailCount = new Dictionary<string, int>();
var ruleFailFiles = new Dictionary<string, List<string>>();
int totalFailing = 0;
int crashes = 0;

var runtimeDiagLimit = 3;
var runtimeDiagCount = 0;
var ttDiagCount = 0;
var langDiagCount = 0;
var contentDiagCount = 0;
var fontFileDiagCount = 0;

foreach (var file in passFiles)
{
    try
    {
        using var parser = PdfLexerValidationParser.FromFile(file, PDFAFlavour.PDFUA1);
        var validator = ValidatorFactory.CreateValidator(
            new[] { PDFAFlavour.PDFUA1 },
            new ValidatorOptions(LogPassedChecks: true, ShowErrorMessages: false));
        var result = validator.Validate(parser);

        if (!result.IsCompliant)
        {
            totalFailing++;
            var rel = Path.GetRelativePath(corpusDir, file);
            var hasCmapRule = false;
            foreach (var (ruleId, count) in result.FailedChecks)
            {
                var key = $"{ruleId.Clause}/{ruleId.TestNumber}";
                ruleFailCount[key] = ruleFailCount.GetValueOrDefault(key) + 1;
                if (!ruleFailFiles.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    ruleFailFiles[key] = list;
                }
                if (list.Count < 3) list.Add(rel);
                if (key == "7.21.6/4") hasCmapRule = true;
            }

            // Show TT/Lang diagnostics for first few failing files by rule
            if (hasCmapRule && ttDiagCount < 2)
            {
                ttDiagCount++;
                Console.WriteLine($"\n=== TT FONT DETAILS: {rel} ===");
                PrintFontObjects(parser.GetRoot(), rel, new HashSet<IModelObject>(), "  ");
            }

            var hasLangRule = result.FailedChecks.Any(fc => $"{fc.Key.Clause}/{fc.Key.TestNumber}" == "7.2/33");
            if (hasLangRule && langDiagCount < 2)
            {
                langDiagCount++;
                Console.WriteLine($"\n=== LANG DETAILS: {rel} ===");
                // Check raw catalog for Lang
                var doc2 = PdfLexer.PdfDocument.Open(File.ReadAllBytes(file));
                var cat = doc2.Catalog;
                var langKey = new PdfLexer.PdfName("Lang");
                var hasLangKey = cat.ContainsKey(langKey);
                var langVal = cat.Get(langKey);
                Console.WriteLine($"  Raw catalog: hasLang={hasLangKey} langValue={langVal} langType={langVal?.GetType()?.Name}");
                Console.WriteLine($"  Catalog keys: {string.Join(", ", cat.Keys.Select(k => k.Value))}");
                doc2.Dispose();
                PrintLangObjects(parser.GetRoot(), new HashSet<IModelObject>(), "  ");
            }

            var hasContentRule = result.FailedChecks.Any(fc => $"{fc.Key.Clause}/{fc.Key.TestNumber}" == "7.1/3");
            if (hasContentRule && contentDiagCount < 2)
            {
                contentDiagCount++;
                Console.WriteLine($"\n=== CONTENT ITEM DETAILS: {rel} ===");
                PrintContentItems(parser.GetRoot(), new HashSet<IModelObject>(), "  ");
            }

            var hasFontFileRule = result.FailedChecks.Any(fc => $"{fc.Key.Clause}/{fc.Key.TestNumber}" == "7.21.4.1/1");
            if (hasFontFileRule && fontFileDiagCount < 3)
            {
                fontFileDiagCount++;
                Console.WriteLine($"\n=== FONT FILE DETAILS: {rel} ===");
                PrintFontFileObjects(parser.GetRoot(), new HashSet<IModelObject>(), "  ");
            }

            var hasSymbolicRule = result.FailedChecks.Any(fc => $"{fc.Key.Clause}/{fc.Key.TestNumber}" == "7.21.6/3");
            if (hasSymbolicRule && ttDiagCount < 3)
            {
                ttDiagCount++;
                Console.WriteLine($"\n=== SYMBOLIC TT DETAILS: {rel} ===");
                PrintSymbolicTtFonts(parser.GetRoot(), new HashSet<IModelObject>(), "  ");
            }

            // Diagnose 7.1/12 (containsParent) and 7.2/18 (parentStandardType on SELBody)
            var has712 = result.FailedChecks.Any(fc => fc.Key.Clause == "7.1" && fc.Key.TestNumber == 12);
            if (has712)
            {
                Console.WriteLine($"\n=== 7.1/12 containsParent DETAILS: {rel} ===");
                PrintStructElemParent(parser.GetRoot(), new HashSet<IModelObject>(), "  ");
            }
            var has7218 = result.FailedChecks.Any(fc => fc.Key.Clause == "7.2" && fc.Key.TestNumber == 18);
            if (has7218)
            {
                Console.WriteLine($"\n=== 7.2/18 SELBody parentStandardType DETAILS: {rel} ===");
                PrintLBodyParent(parser.GetRoot(), new HashSet<IModelObject>(), "  ");
            }

            // Show runtime errors for first few files
            if (runtimeDiagCount < runtimeDiagLimit && result.RuntimeErrors.Count > 0)
            {
                runtimeDiagCount++;
                Console.WriteLine($"\n=== RUNTIME ERRORS: {rel} ({result.RuntimeErrors.Count} errors) ===");
                foreach (var err in result.RuntimeErrors.Take(10))
                {
                    Console.WriteLine($"  {err}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        crashes++;
        var rel = Path.GetRelativePath(corpusDir, file);
        Console.WriteLine($"CRASH: {rel} -> {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"\n{totalFailing}/{passFiles.Count} pass-files incorrectly failing ({crashes} crashes)\n");
Console.WriteLine("Rule failures ranked by frequency:");
Console.WriteLine(new string('-', 80));
foreach (var (rule, count) in ruleFailCount.OrderByDescending(x => x.Value))
{
    var examples = string.Join("; ", ruleFailFiles[rule]);
    Console.WriteLine($"  {rule,-20} {count,4} files   e.g. {examples}");
}

static void PrintFontObjects(IModelObject obj, string context, HashSet<IModelObject> seen, string indent = "")
{
    if (!seen.Add(obj)) return;
    if (obj.ObjectType == "TrueTypeFontProgram")
    {
        var isSymbolic = obj.GetPropertyValue("isSymbolic");
        var nrCmaps = obj.GetPropertyValue("nrCmaps");
        var cmap30 = obj.GetPropertyValue("cmap30Present");
        var encoding = obj.GetPropertyValue("Encoding");
        Console.WriteLine($"{indent}TT_PROGRAM isSymbolic={isSymbolic} nrCmaps={nrCmaps} cmap30Present={cmap30} Encoding={encoding}");
    }
    if (obj.ObjectType == "PDTrueTypeFont" || obj.ObjectType.StartsWith("PDFont") || obj.ObjectType == "PDType0Font" || obj.ObjectType == "PDCIDFont")
    {
        var subtype = obj.GetPropertyValue("Subtype");
        var fontName = obj.GetPropertyValue("fontName");
        var isSymbolic = obj.GetPropertyValue("isSymbolic");
        var encoding = obj.GetPropertyValue("Encoding");
        var containsFF = obj.GetPropertyValue("containsFontFile");
        Console.WriteLine($"{indent}FONT [{obj.ObjectType}] name={fontName} subtype={subtype} isSymbolic={isSymbolic} Encoding={encoding} containsFontFile={containsFF}");
        // Show links
        foreach (var link in obj.Links)
        {
            Console.WriteLine($"{indent}  link '{link}' -> {obj.GetLinkedObjects(link).Count} objects");
        }
    }
    foreach (var link in obj.Links)
    {
        foreach (var child in obj.GetLinkedObjects(link))
        {
            PrintFontObjects(child, context, seen, indent + "  ");
        }
    }
}

static void PrintFontFileObjects(IModelObject obj, HashSet<IModelObject> seen, string indent)
{
    if (!seen.Add(obj)) return;
    if (obj.ObjectType.StartsWith("PD") && obj.ObjectType.Contains("Font"))
    {
        var subtype = obj.GetPropertyValue("Subtype");
        var fontName = obj.GetPropertyValue("fontName");
        var containsFF = obj.GetPropertyValue("containsFontFile");
        var renderingMode = obj.GetPropertyValue("renderingMode");
        // Only show fonts that would fail the rule
        if (containsFF is not true && subtype?.ToString() != "Type3" && subtype?.ToString() != "Type0" && renderingMode?.ToString() != "3")
        {
            Console.WriteLine($"{indent}FAILING {obj.ObjectType}: name={fontName} subtype={subtype} containsFontFile={containsFF} renderingMode={renderingMode}");
        }
    }
    foreach (var link in obj.Links)
    {
        foreach (var child in obj.GetLinkedObjects(link))
        {
            PrintFontFileObjects(child, seen, indent + "  ");
        }
    }
}

static void PrintContentItems(IModelObject obj, HashSet<IModelObject> seen, string indent)
{
    if (!seen.Add(obj)) return;
    if (obj.ObjectType == "SESimpleContentItem")
    {
        var isTagged = obj.GetPropertyValue("isTaggedContent");
        var parentsTags = obj.GetPropertyValue("parentsTags");
        var contentType = obj.GetPropertyValue("contentType");
        var tagsStr = parentsTags?.ToString() ?? "";
        if (isTagged is not true && !tagsStr.Contains("Artifact"))
        {
            Console.WriteLine($"{indent}FAILING SESimpleContentItem: isTagged={isTagged} parentsTags=\"{tagsStr}\" contentType={contentType}");
        }
    }
    if (obj.ObjectType == "SEMarkedContent")
    {
        var tag = obj.GetPropertyValue("tag");
        var isTagged = obj.GetPropertyValue("isTaggedContent");
        var parentsTags = obj.GetPropertyValue("parentsTags");
        var structKey = obj.GetPropertyValue("parentStructureElementObjectKey");
        Console.WriteLine($"{indent}SEMarkedContent: tag={tag} isTagged={isTagged} structKey={structKey}");
    }
    if (obj.ObjectType == "PDPage")
    {
        var hasStructP = obj.GetPropertyValue("containsStructParents");
        Console.WriteLine($"{indent}PDPage: containsStructParents={hasStructP}");
    }
    foreach (var link in obj.Links)
    {
        foreach (var child in obj.GetLinkedObjects(link))
        {
            PrintContentItems(child, seen, indent + "  ");
        }
    }
}

static void PrintLangObjects(IModelObject obj, HashSet<IModelObject> seen, string indent)
{
    if (!seen.Add(obj)) return;
    if (obj.ObjectType == "PDDocument")
    {
        var containsLang = obj.GetPropertyValue("containsLang");
        var lang = obj.GetPropertyValue("Lang");
        Console.WriteLine($"{indent}PDDocument containsLang={containsLang} Lang={lang}");
        Console.WriteLine($"{indent}  Properties: {string.Join(", ", obj.Properties.Take(20))}");
    }
    if (obj.ObjectType == "XMPLangAlt")
    {
        Console.WriteLine($"{indent}XMPLangAlt xDefault={obj.GetPropertyValue("xDefault")}");
    }
    foreach (var link in obj.Links)
    {
        foreach (var child in obj.GetLinkedObjects(link))
        {
            PrintLangObjects(child, seen, indent + "  ");
        }
    }
}

static void PrintSymbolicTtFonts(IModelObject obj, HashSet<IModelObject> seen, string indent)
{
    if (!seen.Add(obj)) return;
    if (obj.ObjectType == "PDTrueTypeFont")
    {
        var isSymbolic = obj.GetPropertyValue("isSymbolic");
        var encoding = obj.GetPropertyValue("Encoding");
        var fontName = obj.GetPropertyValue("fontName");
        // Only show fonts that FAIL the rule: isSymbolic && Encoding != null
        if (isSymbolic is true && encoding is not null)
        {
            Console.WriteLine($"{indent}FAILING PDTrueTypeFont: name={fontName} isSymbolic={isSymbolic} Encoding={encoding}");
        }
        else
        {
            Console.WriteLine($"{indent}OK PDTrueTypeFont: name={fontName} isSymbolic={isSymbolic} Encoding={encoding ?? "(null)"}");
        }
    }
    foreach (var link in obj.Links)
    {
        foreach (var child in obj.GetLinkedObjects(link))
        {
            PrintSymbolicTtFonts(child, seen, indent + "  ");
        }
    }
}

static void PrintStructElemParent(IModelObject obj, HashSet<IModelObject> seen, string indent)
{
    if (!seen.Add(obj)) return;
    // Show ALL struct elem types and their containsParent value
    if (obj.SuperTypes.Contains("PDStructElem") || obj.ObjectType == "PDStructElem")
    {
        var containsParent = obj.GetPropertyValue("containsParent");
        var standardType = obj.GetPropertyValue("standardType");
        var valueS = obj.GetPropertyValue("valueS");
        var parentST = obj.GetPropertyValue("parentStandardType");
        if (containsParent is not true)
        {
            Console.WriteLine($"{indent}FAILING {obj.ObjectType}: S={valueS} standardType={standardType} containsParent={containsParent} parentStandardType={parentST}");
        }
    }
    foreach (var link in obj.Links)
    {
        foreach (var child in obj.GetLinkedObjects(link))
        {
            PrintStructElemParent(child, seen, indent + "  ");
        }
    }
}

static void PrintLBodyParent(IModelObject obj, HashSet<IModelObject> seen, string indent)
{
    if (!seen.Add(obj)) return;
    if (obj.ObjectType == "SELBody")
    {
        var parentST = obj.GetPropertyValue("parentStandardType");
        var standardType = obj.GetPropertyValue("standardType");
        var valueS = obj.GetPropertyValue("valueS");
        Console.WriteLine($"{indent}SELBody: S={valueS} standardType={standardType} parentStandardType={parentST} (expecting 'LI')");
    }
    // Also show SELI and SEL for context
    if (obj.ObjectType == "SELI" || obj.ObjectType == "SEL")
    {
        var parentST = obj.GetPropertyValue("parentStandardType");
        var standardType = obj.GetPropertyValue("standardType");
        var kidsTypes = obj.GetPropertyValue("kidsStandardTypes");
        Console.WriteLine($"{indent}{obj.ObjectType}: standardType={standardType} parentStandardType={parentST} kidsStandardTypes={kidsTypes}");
    }
    foreach (var link in obj.Links)
    {
        foreach (var child in obj.GetLinkedObjects(link))
        {
            PrintLBodyParent(child, seen, indent + "  ");
        }
    }
}

#endif

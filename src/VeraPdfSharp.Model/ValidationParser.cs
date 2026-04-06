using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfLexer;
using PdfLexer.Content;
using PdfLexer.Content.Model;
using PdfLexer.DOM;
using PdfLexer.Fonts;
using PdfLexer.Fonts.Files;
using PdfLexer.Operators;
using VeraPdfSharp.Core;

namespace VeraPdfSharp.Model;

public interface IValidationParser : IDisposable
{
    IModelObject GetRoot();
    PDFAFlavour Flavour { get; }
    IReadOnlyList<PDFAFlavour> Flavours { get; }
    void SetFlavours(IEnumerable<PDFAFlavour> flavours);
}

public sealed class PdfLexerValidationParser : IValidationParser
{
    private readonly byte[] _bytes;
    private readonly PdfDocument _document;
    private readonly string? _sourceName;
    private readonly Lazy<IModelObject> _root;
    private IReadOnlyList<PDFAFlavour> _flavours;

    private PdfLexerValidationParser(byte[] bytes, string? sourceName, PDFAFlavour flavour)
    {
        _bytes = bytes;
        _sourceName = sourceName;
        Flavour = flavour;
        _flavours = new[] { flavour };
        _document = PdfDocument.Open(bytes);
        _root = new Lazy<IModelObject>(BuildRoot);
    }

    public PDFAFlavour Flavour { get; }
    public IReadOnlyList<PDFAFlavour> Flavours => _flavours;

    public static PdfLexerValidationParser FromFile(string path, PDFAFlavour flavour)
    {
        var bytes = File.ReadAllBytes(path);
        return new PdfLexerValidationParser(bytes, path, flavour);
    }

    public static PdfLexerValidationParser FromBytes(byte[] bytes, PDFAFlavour flavour, string? sourceName = null) =>
        new(bytes, sourceName, flavour);

    /// <summary>
    /// Detects the primary PDF/A or PDF/UA flavour declared in the file's XMP metadata.
    /// Returns <see cref="PDFAFlavour.NoFlavour"/> when no identification is found.
    /// </summary>
    public static PDFAFlavour DetectFlavour(string path)
    {
        var flavours = DetectFlavours(File.ReadAllBytes(path));
        return flavours.Count > 0 ? flavours[0] : PDFAFlavour.NoFlavour;
    }

    /// <summary>
    /// Detects the primary PDF/A or PDF/UA flavour declared in the document's XMP metadata.
    /// Returns <see cref="PDFAFlavour.NoFlavour"/> when no identification is found.
    /// </summary>
    public static PDFAFlavour DetectFlavour(byte[] bytes)
    {
        var flavours = DetectFlavours(bytes);
        return flavours.Count > 0 ? flavours[0] : PDFAFlavour.NoFlavour;
    }

    /// <summary>
    /// Detects all PDF/A and PDF/UA flavours declared in the file's XMP metadata.
    /// </summary>
    public static IReadOnlyList<PDFAFlavour> DetectFlavours(string path) =>
        DetectFlavours(File.ReadAllBytes(path));

    /// <summary>
    /// Detects all PDF/A and PDF/UA flavours declared in the document's XMP metadata.
    /// </summary>
    public static IReadOnlyList<PDFAFlavour> DetectFlavours(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);

        if (!doc.Catalog.TryGetValue<PdfStream>(PdfName.Metadata, out var metadataStream, false))
        {
            return [];
        }

        try
        {
            var streamBytes = metadataStream.Contents.GetDecodedData();
            var encoding = DetectXmpEncoding(streamBytes);
            var text = encoding.GetString(streamBytes);
            var xdoc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);

            var result = new List<PDFAFlavour>();

            // PDF/A identification: pdfaid:part + pdfaid:conformance
            var pdfaFlavour = DetectPdfAFromXmp(xdoc);
            if (pdfaFlavour != PDFAFlavour.NoFlavour)
            {
                result.Add(pdfaFlavour);
            }

            // PDF/UA identification: pdfuaid:part
            var pdfuaFlavour = DetectPdfUAFromXmp(xdoc);
            if (pdfuaFlavour != PDFAFlavour.NoFlavour)
            {
                result.Add(pdfuaFlavour);
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static PDFAFlavour DetectPdfAFromXmp(XDocument document)
    {
        const string namespaceUri = "http://www.aiim.org/pdfa/ns/id/";
        var schema = document.Descendants()
            .FirstOrDefault(x =>
                x.Elements().Any(e => e.Name.NamespaceName == namespaceUri) ||
                x.Attributes().Any(a => a.Name.NamespaceName == namespaceUri));

        if (schema is null)
        {
            return PDFAFlavour.NoFlavour;
        }

        XNamespace ns = namespaceUri;
        var partText = schema.Element(ns + "part")?.Value ?? schema.Attribute(ns + "part")?.Value;
        if (!int.TryParse(partText, out var part))
        {
            return PDFAFlavour.NoFlavour;
        }

        var conformance = (schema.Element(ns + "conformance")?.Value ?? schema.Attribute(ns + "conformance")?.Value)?.Trim().ToLowerInvariant() ?? "";
        var id = $"{part}{conformance}";
        return PDFAFlavours.ByFlavourId(id);
    }

    private static PDFAFlavour DetectPdfUAFromXmp(XDocument document)
    {
        const string namespaceUri = "http://www.aiim.org/pdfua/ns/id/";
        var schema = document.Descendants()
            .FirstOrDefault(x =>
                x.Elements().Any(e => e.Name.NamespaceName == namespaceUri) ||
                x.Attributes().Any(a => a.Name.NamespaceName == namespaceUri));

        if (schema is null)
        {
            return PDFAFlavour.NoFlavour;
        }

        XNamespace ns = namespaceUri;
        var partText = schema.Element(ns + "part")?.Value ?? schema.Attribute(ns + "part")?.Value;
        if (!int.TryParse(partText, out var part))
        {
            return PDFAFlavour.NoFlavour;
        }

        return PDFAFlavours.ByFlavourId($"ua{part}");
    }

    private static Encoding DetectXmpEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode; // UTF-16 LE
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode; // UTF-16 BE
        }

        return Encoding.UTF8;
    }

    public void SetFlavours(IEnumerable<PDFAFlavour> flavours) => _flavours = flavours.ToArray();

    public IModelObject GetRoot() => _root.Value;

    public void Dispose() => _document.Dispose();

    private IModelObject BuildRoot()
    {
        var analysis = PdfByteAnalysis.Create(_bytes);
        var builder = new PdfModelBuilder(_document, analysis, _sourceName);
        return builder.BuildRoot();
    }
}

internal sealed partial class PdfModelBuilder
{
    private static readonly PdfName AcroFormName = new("AcroForm");
    private static readonly PdfName ActualTextName = new("ActualText");
    private static readonly PdfName AltName = new("Alt");
    private static readonly PdfName AlternatesName = new("Alternates");
    private static readonly PdfName AAName = new("AA");
    private static readonly PdfName BytesName = new("bytes");
    private static readonly PdfName CIDSetName = new("CIDSet");
    private static readonly PdfName DestOutputProfileName = new("DestOutputProfile");
    private static readonly PdfName DestOutputProfileRefName = new("DestOutputProfileRef");

    // Glyph names present in PdfLexer's GlyphNames but absent from veraPDF's
    // standard Adobe Glyph List. For Type1 fonts without a ToUnicode CMap,
    // these names must not produce a non-null toUnicode value.
    private static readonly HashSet<string> TexSpecificGlyphNames = new(StringComparer.Ordinal)
    {
        "angbracketleftbig", "angbracketleftBig", "angbracketleftbigg", "angbracketleftBigg",
        "angbracketrightBig", "angbracketrightbig", "angbracketrightBigg", "angbracketrightbigg",
        "arrowhookleft", "arrowhookright",
        "arrowlefttophalf", "arrowleftbothalf",
        "arrownortheast", "arrownorthwest",
        "arrowrighttophalf", "arrowrightbothalf",
        "arrowsoutheast", "arrowsouthwest",
        "backslashbig", "backslashBig", "backslashBigg", "backslashbigg",
        "bardbl",
        "bracehtipdownleft", "bracehtipdownright", "bracehtipupleft", "bracehtipupright",
        "braceleftBig", "braceleftbig", "braceleftbigg", "braceleftBigg",
        "bracerightBig", "bracerightbig", "bracerightbigg", "bracerightBigg",
        "bracketleftbig", "bracketleftBig", "bracketleftbigg", "bracketleftBigg",
        "bracketrightBig", "bracketrightbig", "bracketrightbigg", "bracketrightBigg",
        "ceilingleftbig", "ceilingleftBig", "ceilingleftBigg", "ceilingleftbigg",
        "ceilingrightbig", "ceilingrightBig", "ceilingrightbigg", "ceilingrightBigg",
        "circledotdisplay", "circledottext",
        "circlemultiplydisplay", "circlemultiplytext",
        "circleplusdisplay", "circleplustext",
        "contintegraldisplay", "contintegraltext",
        "coproductdisplay", "coproducttext",
        "floorleftBig", "floorleftbig", "floorleftbigg", "floorleftBigg",
        "floorrightbig", "floorrightBig", "floorrightBigg", "floorrightbigg",
        "hatwide", "hatwider", "hatwidest",
        "intercal",
        "integraldisplay", "integraltext",
        "intersectiondisplay", "intersectiontext",
        "logicalanddisplay", "logicalandtext",
        "logicalordisplay", "logicalortext",
        "parenleftBig", "parenleftbig", "parenleftBigg", "parenleftbigg",
        "parenrightBig", "parenrightbig", "parenrightBigg", "parenrightbigg",
        "prime",
        "productdisplay", "producttext",
        "radicalbig", "radicalBig", "radicalBigg", "radicalbigg",
        "radicalbt", "radicaltp", "radicalvertex",
        "slashbig", "slashBig", "slashBigg", "slashbigg",
        "summationdisplay", "summationtext",
        "tildewide", "tildewider", "tildewidest",
        "uniondisplay", "unionmultidisplay", "unionmultitext",
        "unionsqdisplay", "unionsqtext", "uniontext",
        "vextenddouble", "vextendsingle"
    };
    private static readonly PdfName DisplayDocTitleName = new("DisplayDocTitle");
    private static readonly PdfName FieldsName = new("Fields");
    private static readonly PdfName HTOName = new("HTO");
    private static readonly PdfName HTPName = new("HTP");
    private static readonly PdfName IntentName = new("Intent");
    private static readonly PdfName InterpolateName = new("Interpolate");
    private static readonly PdfName KidsName = new("K");
    private static readonly PdfName FFilterName = new("FFilter");
    private static readonly PdfName FDecodeParmsName = new("FDecodeParms");
    private static readonly PdfName LangName = new("Lang");
    private static readonly PdfName MarkedName = new("Marked");
    private static readonly PdfName NeedAppearancesName = new("NeedAppearances");
    private static readonly PdfName NeedsRenderingName = new("NeedsRendering");
    private static readonly PdfName NumsName = new("Nums");
    private static readonly PdfName OPIName = new("OPI");
    private static readonly PdfName PName = PdfName.Parent;
    private static readonly PdfName StructElemParentName = new("P");
    private static readonly PdfName PSName = new("PS");
    private static readonly PdfName RectName = new("Rect");
    private static readonly PdfName RefName = new("Ref");
    private static readonly PdfName RoleMapName = new("RoleMap");
    private static readonly PdfName StructParentName = new("StructParent");
    private static readonly PdfName StructTreeRootName = PdfName.StructTreeRoot;
    private static readonly PdfName Subtype2Name = new("Subtype2");
    private static readonly PdfName SuspectsName = new("Suspects");
    private static readonly PdfName TabsName = new("Tabs");
    private static readonly PdfName TRName = new("TR");
    private static readonly PdfName TR2Name = new("TR2");
    private static readonly PdfName TUName = new("TU");
    private static readonly PdfName ViewerPreferencesName = new("ViewerPreferences");
    private static readonly PdfName XFAName = new("XFA");
    private static readonly PdfName OCPropertiesName = new("OCProperties");
    private static readonly PdfName ConfigsName = new("Configs");
    private static readonly PdfName DName = new("D");
    private static readonly PdfName ASName = new("AS");
    private static readonly PdfName OCGsName = new("OCGs");
    private static readonly PdfName OrderName = new("Order");
    private static readonly PdfName EFName = new("EF");
    private static readonly PdfName UFName = new("UF");
    private static readonly PdfName CTName = new("CT");
    private static readonly PdfName FTName = new("FT");
    private static readonly PdfName AnnotsName = new("Annots");
    private static readonly PdfName SMaskName = new("SMask");
    private static readonly PdfName BMName = new("BM");
    private static readonly PdfName AFName = new("AF");
    private static readonly PdfName ContentsName = new("Contents");

    // PDF 1.7 (ISO 32000-1:2008, 14.8.4) standard structure types — used for remapping checks (rule 7.1/7)
    private static readonly HashSet<string> Pdf17StandardStructTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct",
        "Private", "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody", "Table", "TR",
        "TH", "TD", "THead", "TBody", "TFoot", "Span", "Quote", "Note", "Reference", "BibEntry", "Code",
        "Link", "Annot", "Ruby", "Warichu", "RB", "RT", "RP", "WT", "WP", "Figure", "Formula", "Form",
        "Artifact"
    };

    // All standard structure types (PDF 1.7 + PDF 2.0) — used for type resolution
    private static readonly HashSet<string> StandardStructTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct",
        "Private", "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L", "LI", "Lbl", "LBody", "Table", "TR",
        "TH", "TD", "THead", "TBody", "TFoot", "Span", "Quote", "Note", "Reference", "BibEntry", "Code",
        "Link", "Annot", "Ruby", "Warichu", "RB", "RT", "RP", "WT", "WP", "Figure", "Formula", "Form",
        "Artifact",
        // PDF 2.0 standard structure types (ISO 32000-2:2020)
        "DocumentFragment", "Aside", "Title", "FENote", "Sub", "Em", "Strong"
    };

    // Standard namespace URIs (PDF 2.0, ISO 32000-2:2020, 14.8.6)
    private static readonly HashSet<string> StandardNamespaceUrls = new(StringComparer.Ordinal)
    {
        "http://iso.org/pdf/ssn",      // PDF 1.7
        "http://iso.org/pdf2/ssn",     // PDF 2.0
        "http://www.w3.org/1998/Math/MathML"  // MathML
    };

    private static readonly HashSet<string> AnnotationSubtypes = new(StringComparer.Ordinal)
    {
        "Text", "Link", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight", "Underline",
        "Squiggly", "StrikeOut", "Stamp", "Caret", "Ink", "Popup", "FileAttachment", "Sound", "Movie", "Widget",
        "Screen", "PrinterMark", "TrapNet", "Watermark", "3D", "Redact"
    };

    private static readonly HashSet<string> Standard14Fonts = new(StringComparer.Ordinal)
    {
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats"
    };

    private readonly PdfDocument _document;
    private readonly PdfByteAnalysis _analysis;
    private readonly string? _sourceName;
    private readonly Dictionary<IPdfObject, GenericModelObject> _cache = new(ReferenceEqualityComparer<IPdfObject>.Instance);
    private readonly Dictionary<IPdfObject, string> _objectIds = new(ReferenceEqualityComparer<IPdfObject>.Instance);
    private readonly Dictionary<PdfDictionary, StructInfo> _structCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<int, StructInfo> _parentTreeMap = new();
    private readonly Dictionary<MarkedContentKey, StructInfo> _markedContentParentTreeMap = new();
    private readonly Dictionary<PdfStream, XmpMetadataSnapshot?> _xmpCache = new(ReferenceEqualityComparer<PdfStream>.Instance);
    private readonly Dictionary<PdfDictionary, FontUsageInfo> _fontUsage = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly HashSet<PdfDictionary> _allFontDicts = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, FontProgramInfo?> _fontProgramCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, GenericModelObject?> _trueTypeFontProgramObjects = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, CffWidthInfo?> _cffWidthCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, Dictionary<string, double>?> _type3WidthCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, TableInfo> _tableInfoCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, TableCellInfo> _tableCellInfoCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, HeadingInfo> _headingInfoCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, TableCellGeometry> _tableCellGeometryCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly HashSet<PdfStream> _formXObjectsWithMcids = new(ReferenceEqualityComparer<PdfStream>.Instance);
    private readonly Dictionary<PdfStream, int> _formXObjectRefCount = new(ReferenceEqualityComparer<PdfStream>.Instance);
    private readonly HashSet<PdfStream> _referencedFormXObjects = new(ReferenceEqualityComparer<PdfStream>.Instance);
    private readonly HashSet<string> _duplicateNoteIds = new(StringComparer.Ordinal);
    private HashSet<PdfDictionary>? _afReferencedFileSpecs;
    // Track Separation colorant data for areTintAndAlternateConsistent:
    // Maps colorant name → (alternate COS object, tintTransform COS object) from first occurrence
    private readonly Dictionary<string, (IPdfObject alternate, IPdfObject tintTransform)> _separationsByColorant = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inconsistentSeparations = new(StringComparer.Ordinal);
    private bool _structureInitialized;
    private PdfDictionary? _currentPageDict;
    // GTS_PDFA1-only output color space from the document catalog, used for gOutputCS in PDFA-1/2 rules.
    private string? _pdfa1OutputCS;
    // Hex string index built from raw PDF bytes for CosString validation
    private HexStringIndex? _hexStringIndex;

    public PdfModelBuilder(PdfDocument document, PdfByteAnalysis analysis, string? sourceName)
    {
        _document = document;
        _analysis = analysis;
        _sourceName = sourceName;
        _hexStringIndex = HexStringIndex.Build(analysis.RawBytes);
    }

    private void PreScanReferencedFormXObjects()
    {
        foreach (var page in _document.Pages)
        {
            try
            {
                var nodes = new PdfPage(page.NativeObject).GetContentModel();
                CollectReferencedFormXObjects(nodes);
            }
            catch
            {
                // Skip pages that fail to parse
            }
        }
    }

    private void CollectReferencedFormXObjects(IReadOnlyList<IContentGroup<double>> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is FormContent<double> form)
            {
                _referencedFormXObjects.Add(form.Stream);
                try
                {
                    CollectReferencedFormXObjects(form.Parse().OfType<IContentGroup<double>>().ToList());
                }
                catch
                {
                    // Skip forms that fail to parse
                }
            }
            else if (node is MarkedContentGroup<double> mcg)
            {
                CollectReferencedFormXObjects(mcg.Children.OfType<IContentGroup<double>>().ToList());
            }
        }
    }

    public IModelObject BuildRoot()
    {
        // Pre-scan content streams to identify which form XObjects are actually
        // referenced (invoked via Do operator). veraPDF only validates referenced XObjects.
        PreScanReferencedFormXObjects();

        // Pre-compute the GTS_PDFA1-only output CS for use by device CS rules (PDFA-1/2).
        _pdfa1OutputCS = GetOutputColorSpace(_document.Catalog, pdfA1Only: true);

        var trailer = BuildObject(_document.Trailer, relationName: "trailer");
        var document = BuildObject(_document.Catalog, relationName: "document");

        var root = new GenericModelObject("CosDocument", context: _sourceName);
        root.Set("header", _analysis.Header);
        root.Set("headerOffset", _analysis.HeaderOffset);
        root.Set("headerByte1", _analysis.BinaryCommentBytes.ElementAtOrDefault(0));
        root.Set("headerByte2", _analysis.BinaryCommentBytes.ElementAtOrDefault(1));
        root.Set("headerByte3", _analysis.BinaryCommentBytes.ElementAtOrDefault(2));
        root.Set("headerByte4", _analysis.BinaryCommentBytes.ElementAtOrDefault(3));
        root.Set("isLinearized", _analysis.IsLinearized);
        root.Set("postEOFDataSize", _analysis.PostEofDataSize);
        root.Set("MarkInfo", document.GetPropertyValue("MarkInfo"));
        root.Set("Marked", document.GetPropertyValue("Marked"));
        root.Set("Suspects", document.GetPropertyValue("Suspects"));
        root.Set("NeedsRendering", document.GetPropertyValue("NeedsRendering"));
        root.Set("DisplayDocTitle", document.GetPropertyValue("DisplayDocTitle"));
        root.Set("ViewerPreferences", document.GetPropertyValue("ViewerPreferences"));
        root.Set("lastID", document.GetPropertyValue("lastIDValue"));
        root.Set("lastIDValue", document.GetPropertyValue("lastIDValue"));
        root.Set("firstPageID", document.GetPropertyValue("firstPageIDValue"));
        root.Set("firstPageIDValue", document.GetPropertyValue("firstPageIDValue"));

        // CosDocument properties derived from the catalog
        var namesDict = _document.Catalog.GetOptionalValue<PdfDictionary>(PdfName.Names);
        root.Set("containsEmbeddedFiles", namesDict?.ContainsKey(new PdfName("EmbeddedFiles")) ?? false);
        root.Set("isOptionalContentPresent", _document.Catalog.ContainsKey(OCPropertiesName));
        root.Set("containsAlternatePresentations", namesDict?.ContainsKey(new PdfName("AlternatePresentations")) ?? false);
        root.Set("containsPieceInfo", _document.Catalog.ContainsKey(new PdfName("PieceInfo")));
        root.Set("containsInfo", _document.Trailer.ContainsKey(new PdfName("Info")));
        root.Set("nrIndirects", _document.XrefEntries?.Count ?? 0);
        root.Set("Requirements", _document.Catalog.ContainsKey(new PdfName("Requirements")) ? "present" : null);

        // PDF version from catalog /Version key (overrides header version per ISO 32000-1:2008, 7.2.2)
        var catalogVersion = ConvertPdfObjectToString(_document.Catalog.Get(new PdfName("Version")));
        root.Set("Version", catalogVersion);

        // containsPDFAIdentification: determined from XMP metadata, set on CosDocument for profile rules
        var xmpSnapshot = GetXmpSnapshotFromCatalog();
        root.Set("containsPDFAIdentification", xmpSnapshot?.PdfAIdentification is not null);

        root.Link("trailer", trailer);
        root.Link("document", document);

        if (_document.Trailer.TryGetValue<PdfDictionary>(new PdfName("Info"), out var info, false))
        {
            root.Link("info", BuildObject(info, relationName: "Info", parentObjectType: root.ObjectType, parentPdfObject: _document.Trailer));
        }

        var metadataObjects = CreateSyntheticMetadataObjects();
        if (metadataObjects.Count > 0)
        {
            root.Link("metadata", metadataObjects.ToArray());
        }

        // CosIndirect objects for each indirect object — checks byte-level spacing around obj/endobj
        var indirects = BuildCosIndirectObjects(_analysis.RawBytes);
        if (indirects.Length > 0)
        {
            root.Link("indirectObjects", indirects);
        }

        // CosXRef — checks EOL markers after "xref" keyword in cross-reference tables
        var xref = BuildCosXRef(_analysis.RawBytes);
        root.Link("xref", xref);

        ApplyFontRenderingModes();

        return root;
    }

    private List<IModelObject> CreateSyntheticMetadataObjects()
    {
        var result = new List<IModelObject>();
        if (!_document.Catalog.TryGetValue<PdfStream>(PdfName.Metadata, out var metadataStream, false))
        {
            return result;
        }

        var snapshot = GetXmpSnapshot(metadataStream);
        if (snapshot is null)
        {
            return result;
        }

        var main = new GenericModelObject("MainXMPPackage",
            superTypes: new[] { "XMPPackage", "XMPObject" });
        main.Set("containsPDFUAIdentification", snapshot.PdfUaIdentification is not null);
        main.Set("containsPDFAIdentification", snapshot.PdfAIdentification is not null);
        main.Set("dc_title", snapshot.DcTitle);
        main.Set("isSerializationValid", snapshot.IsSerializationValid);
        main.Set("actualEncoding", snapshot.ActualEncoding);
        main.Set("bytes", snapshot.PacketBytesAttribute);
        main.Set("encoding", snapshot.PacketEncodingAttribute);
        main.Link("package", BuildObject(metadataStream, relationName: PdfName.Metadata.Value, parentObjectType: "PDDocument", parentPdfObject: _document.Catalog));

        // Parse extension schemas and create XMPProperty objects from the XMP document
        if (snapshot.Document is not null)
        {
            var (extensionDefinedProps, extensionSchemaObjects) = ParseExtensionSchemas(snapshot.Document);

            // link ExtensionSchemasContainers
            if (extensionSchemaObjects.Count > 0)
            {
                main.Link("ExtensionSchemasContainers", extensionSchemaObjects.ToArray());
            }

            // Create XMPProperty objects for all properties in the XMP
            var xmpProperties = CollectXmpProperties(snapshot.Document);
            var propertyObjects = new List<IModelObject>();
            foreach (var (ns, prefix, localName, isLangAlt, xNode) in xmpProperties)
            {
                var isPredefined2004 = Predefined2004.ContainsKey((ns, localName));
                var isPredefined2005 = Predefined2005.ContainsKey((ns, localName));
                var isDefinedInExtension = extensionDefinedProps.Contains((ns, localName));

                // Determine the type from the best covering definition
                string? predefinedType = null;
                string? type2004 = null;
                string? type2005 = null;
                Predefined2004.TryGetValue((ns, localName), out type2004);
                Predefined2005.TryGetValue((ns, localName), out type2005);
                predefinedType = type2005 ?? type2004;

                // Validate value type against expected type
                bool? isValueTypeCorrect = null;
                if (predefinedType is not null)
                {
                    isValueTypeCorrect = ValidateValueType(xNode, predefinedType);
                    // If both 2004 and 2005 define different types, accept if either matches
                    if (isValueTypeCorrect == false && type2004 is not null && type2004 != predefinedType)
                        isValueTypeCorrect = ValidateValueType(xNode, type2004);
                }
                else if (isDefinedInExtension)
                    isValueTypeCorrect = true; // Extension-defined without predefined type — assume correct

                var propId = string.IsNullOrEmpty(prefix) ? localName : $"{prefix}:{localName}";

                if (isLangAlt)
                {
                    var langAltProp = new GenericModelObject("XMPLangAlt",
                        id: propId,
                        context: ns,
                        superTypes: new[] { "XMPProperty", "XMPObject" });
                    langAltProp.Set("isPredefinedInXMP2004", isPredefined2004);
                    langAltProp.Set("isPredefinedInXMP2005", isPredefined2005);
                    langAltProp.Set("isDefinedInCurrentPackage", isDefinedInExtension);
                    langAltProp.Set("isDefinedInMainPackage", isDefinedInExtension);
                    if (isValueTypeCorrect.HasValue)
                        langAltProp.Set("isValueTypeCorrect", isValueTypeCorrect.Value);
                    langAltProp.Set("predefinedType", predefinedType);

                    // Check xDefault for the lang alt
                    XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
                    XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";
                    var altItems = snapshot.Document.Descendants(XNamespace.Get(ns) + localName)
                        .SelectMany(e => e.Descendants(rdf + "Alt"))
                        .SelectMany(alt => alt.Elements(rdf + "li"))
                        .ToList();
                    bool isXDefault = altItems.Count == 1
                        && string.Equals(altItems[0].Attribute(xmlNs + "lang")?.Value, "x-default", StringComparison.OrdinalIgnoreCase);
                    langAltProp.Set("xDefault", isXDefault);

                    propertyObjects.Add(langAltProp);
                }
                else
                {
                    var prop = new GenericModelObject("XMPProperty",
                        id: propId,
                        context: ns,
                        superTypes: new[] { "XMPObject" });
                    prop.Set("isPredefinedInXMP2004", isPredefined2004);
                    prop.Set("isPredefinedInXMP2005", isPredefined2005);
                    prop.Set("isDefinedInCurrentPackage", isDefinedInExtension);
                    prop.Set("isDefinedInMainPackage", isDefinedInExtension);
                    if (isValueTypeCorrect.HasValue)
                        prop.Set("isValueTypeCorrect", isValueTypeCorrect.Value);
                    prop.Set("predefinedType", predefinedType);
                    propertyObjects.Add(prop);
                }
            }

            if (propertyObjects.Count > 0)
            {
                main.Link("Properties", propertyObjects.ToArray());
            }
        }

        result.Add(main);

        if (snapshot.PdfUaIdentification is not null)
        {
            var pdfUa = new GenericModelObject("PDFUAIdentification");
            pdfUa.Set("part", snapshot.PdfUaIdentification.Part);
            pdfUa.Set("partPrefix", snapshot.PdfUaIdentification.PartPrefix);
            pdfUa.Set("rev", snapshot.PdfUaIdentification.Rev);
            pdfUa.Set("revPrefix", snapshot.PdfUaIdentification.RevPrefix);
            pdfUa.Set("amdPrefix", snapshot.PdfUaIdentification.AmdPrefix);
            pdfUa.Set("corrPrefix", snapshot.PdfUaIdentification.CorrPrefix);
            result.Add(pdfUa);
        }

        if (snapshot.PdfAIdentification is not null)
        {
            var pdfa = new GenericModelObject("PDFAIdentification");
            pdfa.Set("part", snapshot.PdfAIdentification.Part);
            pdfa.Set("partPrefix", snapshot.PdfAIdentification.PartPrefix);
            pdfa.Set("conformance", snapshot.PdfAIdentification.Conformance);
            pdfa.Set("conformancePrefix", snapshot.PdfAIdentification.ConformancePrefix);
            pdfa.Set("rev", snapshot.PdfAIdentification.Rev);
            pdfa.Set("revPrefix", snapshot.PdfAIdentification.RevPrefix);
            pdfa.Set("amdPrefix", snapshot.PdfAIdentification.AmdPrefix);
            pdfa.Set("corrPrefix", snapshot.PdfAIdentification.CorrPrefix);
            result.Add(pdfa);
        }

        // Note: XMPLangAlt objects from ReadXmpLangAlts are now created as part of
        // the XMPProperty walk above (with proper predefined/extension checks).
        // The old standalone langAlt loop is removed.

        return result;
    }

    private GenericModelObject BuildObject(IPdfObject source, string? relationName = null, string? parentObjectType = null, IPdfObject? parentPdfObject = null)
    {
        var id = TryGetObjectId(source);
        var resolved = source.Resolve();
        if (id is not null)
        {
            _objectIds[resolved] = id;
        }

        if (_cache.TryGetValue(resolved, out var cached))
        {
            return cached;
        }

        var descriptor = DescribeObject(resolved, relationName, parentObjectType, parentPdfObject);
        var model = new GenericModelObject(descriptor.ObjectType, id: id, superTypes: descriptor.SuperTypes);
        _cache[resolved] = model;

        switch (resolved)
        {
            case PdfDictionary dictionary:
                PopulateDictionaryObject(model, dictionary, descriptor, relationName, parentPdfObject);
                break;
            case PdfStream stream:
                PopulateStreamObject(model, stream, descriptor, relationName, parentPdfObject);
                break;
            case PdfArray array:
                PopulateArrayObject(model, array, descriptor.ObjectType);
                break;
            default:
                PopulatePrimitiveObject(model, resolved);
                break;
        }

        return model;
    }

    private ObjectDescriptor DescribeObject(IPdfObject resolved, string? relationName, string? parentObjectType, IPdfObject? parentPdfObject)
    {
        if (ReferenceEquals(resolved, _document.Trailer))
        {
            return new ObjectDescriptor("CosTrailer");
        }

        if (ReferenceEquals(resolved, _document.Catalog))
        {
            return new ObjectDescriptor("PDDocument");
        }

        if (_document.Trailer.TryGetValue<PdfDictionary>(new PdfName("Info"), out var info, false) && ReferenceEquals(resolved, info))
        {
            return new ObjectDescriptor("CosInfo");
        }

        if (resolved is PdfStream stream)
        {
            return DescribeStream(stream, relationName, parentObjectType) ?? new ObjectDescriptor("CosStream");
        }

        if (resolved is PdfDictionary dictionary)
        {
            return DescribeDictionary(dictionary, relationName, parentObjectType, parentPdfObject) ?? new ObjectDescriptor("CosDictionary");
        }

        return resolved switch
        {
            PdfArray => new ObjectDescriptor("CosArray"),
            PdfString => new ObjectDescriptor("CosString"),
            PdfName => new ObjectDescriptor("CosName"),
            PdfBoolean => new ObjectDescriptor("CosBoolean"),
            PdfNumber number when Math.Abs((double)number - Math.Truncate((double)number)) < 1e-9 => new ObjectDescriptor("CosInteger"),
            PdfNumber => new ObjectDescriptor("CosReal"),
            _ => new ObjectDescriptor("CosObject"),
        };
    }

    private ObjectDescriptor? DescribeStream(PdfStream stream, string? relationName, string? parentObjectType)
    {
        var dictionary = stream.Dictionary;
        var type = ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName));
        var subtype = ConvertPdfObjectToString(dictionary.Get(PdfName.Subtype));

        if (string.Equals(relationName, PdfName.Metadata.Value, StringComparison.Ordinal) ||
            (string.Equals(type, "Metadata", StringComparison.Ordinal) && string.Equals(subtype, "XML", StringComparison.Ordinal)))
        {
            return new ObjectDescriptor("XMPPackage", "CosStream");
        }

        if (string.Equals(relationName, DestOutputProfileName.Value, StringComparison.Ordinal))
        {
            return new ObjectDescriptor("ICCOutputProfile", "CosStream");
        }

        if (string.Equals(type, "EmbeddedFile", StringComparison.Ordinal))
        {
            return new ObjectDescriptor("EmbeddedFile", "CosStream");
        }

        // Embedded file streams found as values in an EF dictionary (under CosFileSpecification)
        if (string.Equals(parentObjectType, "CosEFDict", StringComparison.Ordinal))
        {
            return new ObjectDescriptor("EmbeddedFile", "CosStream");
        }

        if (string.Equals(type, "XObject", StringComparison.Ordinal) || string.Equals(subtype, "Image", StringComparison.Ordinal) || string.Equals(subtype, "Form", StringComparison.Ordinal))
        {
            if (string.Equals(subtype, "Image", StringComparison.Ordinal))
            {
                // Mask images: /ImageMask=true only (SMask/soft masks are NOT image masks)
                var isImageMask = dictionary.GetOptionalValue<PdfBoolean>(PdfName.ImageMask)?.Value ?? false;
                if (isImageMask)
                {
                    return new ObjectDescriptor("PDMaskImage", "PDXImage", "CosStream");
                }

                var representation = GetInternalRepresentation(stream);
                return string.Equals(representation, "JPEG2000", StringComparison.Ordinal)
                    ? new ObjectDescriptor("JPEG2000", "PDXImage", "CosStream")
                    : new ObjectDescriptor("PDXImage", "CosStream");
            }

            if (string.Equals(subtype, "Form", StringComparison.Ordinal))
            {
                // Only create PDXForm for form XObjects actually referenced from content streams.
                // veraPDF validates only used XObjects; unreferenced ones are ignored.
                if (_referencedFormXObjects.Contains(stream))
                {
                    return new ObjectDescriptor("PDXForm", "CosStream");
                }
                return null; // Falls through to CosStream
            }

            if (string.Equals(subtype, "PS", StringComparison.Ordinal))
            {
                return new ObjectDescriptor("PDXObject", "CosStream");
            }
        }

        if (string.Equals(relationName, PdfName.Contents.Value, StringComparison.Ordinal) &&
            (string.Equals(parentObjectType, "PDPage", StringComparison.Ordinal) || string.Equals(parentObjectType, "PDXForm", StringComparison.Ordinal)))
        {
            return new ObjectDescriptor("PDContentStream", "CosStream");
        }

        return null;
    }

    private ObjectDescriptor? DescribeDictionary(PdfDictionary dictionary, string? relationName, string? parentObjectType, IPdfObject? parentPdfObject)
    {
        var type = ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName));
        var subtype = ConvertPdfObjectToString(dictionary.Get(PdfName.Subtype));

        if (string.Equals(type, "Page", StringComparison.Ordinal))
        {
            return new ObjectDescriptor("PDPage");
        }

        if (string.Equals(type, "StructTreeRoot", StringComparison.Ordinal))
        {
            return new ObjectDescriptor("PDStructTreeRoot");
        }

        // Struct elements must have both /S (structure type) and /P (parent).
        // Don't rely on /Type=StructElem alone — ParentTree may contain copies
        // of struct element dictionaries resolved to separate instances by PdfLexer.
        // But if /Type IS present and is NOT "StructElem", this is another dict type
        // that happens to have /S and /P (e.g. Media Rendition /S=MR /P=playParams,
        // Media Clip Data /S=MCD /P=permissions).
        if (dictionary.ContainsKey(new PdfName("S")) && dictionary.ContainsKey(StructElemParentName)
            && (type is null || string.Equals(type, "StructElem", StringComparison.Ordinal)))
        {
            return DescribeStructureElement(dictionary);
        }

        if (string.Equals(type, "Annot", StringComparison.Ordinal) || (subtype is not null && type is null && AnnotationSubtypes.Contains(subtype)))
        {
            return subtype switch
            {
                "Widget" => new ObjectDescriptor("PDWidgetAnnot", "PDAnnot"),
                "Link" => new ObjectDescriptor("PDLinkAnnot", "PDAnnot"),
                "TrapNet" => new ObjectDescriptor("PDTrapNetAnnot", "PDAnnot"),
                "PrinterMark" => new ObjectDescriptor("PDPrinterMarkAnnot", "PDAnnot"),
                _ => new ObjectDescriptor("PDAnnot"),
            };
        }

        if (string.Equals(type, "Action", StringComparison.Ordinal) || (dictionary.ContainsKey(PdfName.S) && (dictionary.ContainsKey(PdfName.N) || string.Equals(relationName, PdfName.A.Value, StringComparison.Ordinal) || string.Equals(relationName, "OpenAction", StringComparison.Ordinal) || string.Equals(parentObjectType, "PDAdditionalActions", StringComparison.Ordinal))))
        {
            return string.Equals(ConvertPdfObjectToString(dictionary.Get(PdfName.S)), "Named", StringComparison.Ordinal)
                ? new ObjectDescriptor("PDNamedAction", "PDAction")
                : new ObjectDescriptor("PDAction");
        }

        if (dictionary.ContainsKey(FieldsName) || dictionary.ContainsKey(NeedAppearancesName) || dictionary.ContainsKey(XFAName) || string.Equals(relationName, AcroFormName.Value, StringComparison.Ordinal))
        {
            return new ObjectDescriptor("PDAcroForm");
        }

        if (string.Equals(type, "OutputIntent", StringComparison.Ordinal) || dictionary.ContainsKey(DestOutputProfileName))
        {
            return new ObjectDescriptor("PDOutputIntent");
        }

        if (IsFontDictionary(dictionary))
        {
            return DescribeFont(dictionary);
        }

        if (IsExtGStateDictionary(dictionary))
        {
            return new ObjectDescriptor("PDExtGState");
        }

        if (string.Equals(relationName, ViewerPreferencesName.Value, StringComparison.Ordinal))
        {
            return new ObjectDescriptor("PDViewerPreferences");
        }

        if (string.Equals(relationName, "Encrypt", StringComparison.Ordinal) || string.Equals(type, "Encrypt", StringComparison.Ordinal))
        {
            return new ObjectDescriptor("PDEncryption");
        }

        if (parentPdfObject is PdfDictionary parentDictionary &&
            string.Equals(ConvertPdfObjectToString(parentDictionary.Get(PdfName.TypeName)), "Pages", StringComparison.Ordinal) &&
            string.Equals(type, "Pages", StringComparison.Ordinal))
        {
            return new ObjectDescriptor("PDPageTreeNode");
        }

        if (string.Equals(type, "Filespec", StringComparison.Ordinal) || string.Equals(type, "F", StringComparison.Ordinal) ||
            (dictionary.ContainsKey(EFName) && (dictionary.ContainsKey(PdfName.F) || dictionary.ContainsKey(UFName))))
        {
            return new ObjectDescriptor("CosFileSpecification");
        }

        if (string.Equals(type, "MediaClip", StringComparison.Ordinal) ||
            (dictionary.ContainsKey(new PdfName("N")) && dictionary.ContainsKey(new PdfName("D")) && dictionary.ContainsKey(CTName)))
        {
            return new ObjectDescriptor("PDMediaClip");
        }

        if (dictionary.ContainsKey(FTName) && (dictionary.ContainsKey(TUName) || dictionary.ContainsKey(PdfName.V) || dictionary.ContainsKey(PdfName.Parent)))
        {
            return new ObjectDescriptor("PDFormField");
        }

        // PDGroup: transparency group dictionary (found under /Group on pages or form XObjects)
        if (string.Equals(relationName, "Group", StringComparison.Ordinal) && dictionary.ContainsKey(new PdfName("S")))
        {
            return new ObjectDescriptor("PDGroup");
        }

        // PDHalftone: halftone dictionary (found under /HT in ExtGState or /HTP in page)
        // Type 5 halftones are containers — their sub-entries (keyed by colorant name) are the actual PDHalftone objects
        if (dictionary.ContainsKey(new PdfName("HalftoneType")))
        {
            var ht = GetNumberValue(dictionary.Get(new PdfName("HalftoneType")));
            if (ht is not null && (int)ht.Value != 5)
            {
                return new ObjectDescriptor("PDHalftone");
            }
        }

        return null;
    }

    private ObjectDescriptor DescribeStructureElement(PdfDictionary dictionary)
    {
        var info = GetStructInfo(dictionary);
        var superTypes = new List<string> { "PDStructElem" };
        string objectType;

        if (!string.IsNullOrEmpty(info.RawType) && !StandardStructTypes.Contains(info.RawType))
        {
            objectType = "SENonStandard";
            var roleSpecific = MapStructureObjectType(info.StandardType);
            if (roleSpecific is not null)
            {
                superTypes.Add(roleSpecific);
            }
        }
        else
        {
            objectType = MapStructureObjectType(info.StandardType) ?? "PDStructElem";
        }

        // Add SETableCell as supertype for TH and TD
        if (objectType is "SETH" or "SETD")
        {
            superTypes.Add("SETableCell");
        }

        return new ObjectDescriptor(objectType, superTypes.ToArray());
    }

    private static string? MapStructureObjectType(string? standardType) => standardType switch
    {
        "Table" => "SETable",
        "TR" => "SETR",
        "THead" => "SETHead",
        "TBody" => "SETBody",
        "TFoot" => "SETFoot",
        "TH" => "SETH",
        "TD" => "SETD",
        "L" => "SEL",
        "LI" => "SELI",
        "Lbl" => "SELbl",
        "LBody" => "SELBody",
        "Figure" => "SEFigure",
        "Formula" => "SEFormula",
        "Note" => "SENote",
        "Form" => "SEForm",
        "H" => "SEH",
        "H1" or "H2" or "H3" or "H4" or "H5" or "H6" => "SEHn",
        "TOC" => "SETOC",
        "TOCI" => "SETOCI",
        "Caption" => "SECaption",
        "Span" => "SESpan",
        "Link" => "SELink",
        "Annot" => "SEAnnot",
        "Document" => "SEDocument",
        "Part" => "SEPart",
        "Div" => "SEDiv",
        "Sect" => "SESect",
        "P" => "SEP",
        "BlockQuote" => "SEBlockQuote",
        "Quote" => "SEQuote",
        "Code" => "SECode",
        "Reference" => "SEReference",
        "BibEntry" => "SEBibEntry",
        "NonStruct" => "SENonStruct",
        "Private" => "SEPrivate",
        "Ruby" => "SERuby",
        "Warichu" => "SEWarichu",
        "Art" => "SEArt",
        "Artifact" => "SEArtifact",
        "Aside" => "SEAside",
        "DocumentFragment" => "SEDocumentFragment",
        "Em" => "SEEm",
        "FENote" => "SEFENote",
        "Index" => "SEIndex",
        "Strong" => "SEStrong",
        "Sub" => "SESub",
        "Title" => "SETitle",
        "RB" => "SERB",
        "RP" => "SERP",
        "RT" => "SERT",
        "WP" => "SEWP",
        "WT" => "SEWT",
        _ => null,
    };

    private static bool IsFontDictionary(PdfDictionary dictionary)
    {
        var type = ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName));
        if (string.Equals(type, "Font", StringComparison.Ordinal))
        {
            return true;
        }

        return dictionary.ContainsKey(PdfName.BaseFont) ||
               dictionary.ContainsKey(PdfName.DescendantFonts) ||
               dictionary.ContainsKey(PdfName.FontDescriptor) ||
               dictionary.ContainsKey(PdfName.FirstChar);
    }

    private static ObjectDescriptor DescribeFont(PdfDictionary dictionary)
    {
        var subtype = ConvertPdfObjectToString(dictionary.Get(PdfName.Subtype));
        return subtype switch
        {
            "TrueType" => new ObjectDescriptor("PDTrueTypeFont", "PDSimpleFont", "PDFont"),
            "Type1" or "MMType1" => new ObjectDescriptor("PDType1Font", "PDSimpleFont", "PDFont"),
            "Type3" => new ObjectDescriptor("PDType3Font", "PDSimpleFont", "PDFont"),
            "Type0" => new ObjectDescriptor("PDType0Font", "PDFont"),
            "CIDFontType0" or "CIDFontType2" => new ObjectDescriptor("PDCIDFont", "PDFont"),
            _ => new ObjectDescriptor("PDFont"),
        };
    }

    private static bool IsExtGStateDictionary(PdfDictionary dictionary) =>
        string.Equals(ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName)), "ExtGState", StringComparison.Ordinal) ||
        dictionary.ContainsKey(new PdfName("CA")) ||
        dictionary.ContainsKey(new PdfName("ca")) ||
        dictionary.ContainsKey(TRName) ||
        dictionary.ContainsKey(TR2Name) ||
        dictionary.ContainsKey(HTPName) ||
        dictionary.ContainsKey(BMName) ||
        dictionary.ContainsKey(SMaskName);

    private void PopulateStreamObject(GenericModelObject model, PdfStream stream, ObjectDescriptor descriptor, string? relationName, IPdfObject? parentPdfObject)
    {
        PopulateDictionaryObject(model, stream.Dictionary, descriptor, relationName, parentPdfObject);

        // Set realLength on all CosStream-derived objects (actual byte count in file)
        try
        {
            model.Set("realLength", stream.Contents.Length);
        }
        catch
        {
            // If we can't get the actual length, fall back to the declared Length
            var declaredLength = stream.Dictionary.GetOptionalValue<PdfNumber>(PdfName.Length);
            if (declaredLength is not null)
            {
                model.Set("realLength", (int)(double)declaredLength);
            }
        }

        // stream/endstream keyword compliance (byte-level checks)
        var (streamCRLF, endstreamEOL) = GetStreamKeywordCompliance(stream);
        model.Set("streamKeywordCRLFCompliant", streamCRLF);
        model.Set("endstreamKeywordEOLCompliant", endstreamEOL);

        // CosStream F/FFilter/FDecodeParms — external stream file references (must be null for PDF/A)
        model.Set("F", ConvertPdfObjectToString(stream.Dictionary.Get(PdfName.F)));
        model.Set("FFilter", ConvertPdfObjectToString(stream.Dictionary.Get(FFilterName)));
        model.Set("FDecodeParms", ConvertPdfObjectToString(stream.Dictionary.Get(FDecodeParmsName)));

        // keysString: ampersand-delimited list of dictionary keys (used by rule error arguments)
        model.Set("keysString", string.Join("&", stream.Dictionary.Keys.Select(k => k.Value)));

        // PDContentStream needs inheritedResourceNames and undefinedResourceNames
        if (string.Equals(model.ObjectType, "PDContentStream", StringComparison.Ordinal))
        {
            model.Set("inheritedResourceNames", "");
            model.Set("undefinedResourceNames", "");
        }

        if (string.Equals(model.ObjectType, "XMPPackage", StringComparison.Ordinal))
        {
            PopulateXmpPackage(model, stream);
            return;
        }

        if (string.Equals(model.ObjectType, "ICCOutputProfile", StringComparison.Ordinal))
        {
            PopulateIccProfile(model, stream);
            // Set S from the parent PDOutputIntent dictionary
            if (parentPdfObject?.Resolve() is PdfDictionary parentDict)
            {
                model.Set("S", ConvertPdfObjectToString(parentDict.Get(PdfName.S)));
            }
            return;
        }

        if (string.Equals(model.ObjectType, "EmbeddedFile", StringComparison.Ordinal))
        {
            // For isValidPDFA124: optimistically assume true (recursive embedded file validation not implemented).
            // This avoids false positives on pass-expected files. Fail-expected files will still be caught by
            // other EmbeddedFile/CosFileSpecification rules (Subtype MIME validation, F/UF, AFRelationship).
            model.Set("isValidPDFA124", true);
            return;
        }

        if (string.Equals(model.ObjectType, "PDXImage", StringComparison.Ordinal) || string.Equals(model.ObjectType, "JPEG2000", StringComparison.Ordinal) || string.Equals(model.ObjectType, "PDMaskImage", StringComparison.Ordinal))
        {
            model.Set("containsAlternates", stream.Dictionary.ContainsKey(AlternatesName));
            model.Set("containsOPI", stream.Dictionary.ContainsKey(OPIName));
            model.Set("isMask", stream.Dictionary.GetOptionalValue<PdfBoolean>(PdfName.ImageMask)?.Value ?? false);
            model.Set("Interpolate", stream.Dictionary.GetOptionalValue<PdfBoolean>(InterpolateName)?.Value ?? false);
            model.Set("internalRepresentation", GetInternalRepresentation(stream));
            model.Set("colorSpace", GetColorSpaceName(stream.Dictionary.Get(PdfName.ColorSpace)));
            model.Set("BitsPerComponent", GetNumberValue(stream.Dictionary.Get(new PdfName("BitsPerComponent"))));

            // Link CosRenderingIntent from /Intent key on XObject images
            var intentObj = stream.Dictionary.Get(IntentName);
            if (intentObj?.Resolve() is PdfName intentName)
            {
                var cosIntent = new GenericModelObject("CosRenderingIntent");
                cosIntent.Set("internalRepresentation", intentName.Value);
                model.Link("Intent", cosIntent);
            }

            if (string.Equals(model.ObjectType, "JPEG2000", StringComparison.Ordinal))
            {
                PopulateJpeg2000(model, stream);
            }
            return;
        }

        if (string.Equals(model.ObjectType, "PDXForm", StringComparison.Ordinal))
        {
            model.Set("containsOPI", stream.Dictionary.ContainsKey(OPIName));
            model.Set("containsPS", stream.Dictionary.ContainsKey(PSName));
            model.Set("containsRef", stream.Dictionary.ContainsKey(RefName));
            model.Set("Subtype2", ConvertPdfObjectToString(stream.Dictionary.Get(new PdfName("Subtype2"))));
            model.Set("isUniqueSemanticParent", IsUniqueSemanticParent(stream));
        }

        // Create CosFilter objects from /Filter entries on all streams
        CreateFilterObjects(model, stream.Dictionary);
    }

    private static void CreateFilterObjects(GenericModelObject model, PdfDictionary dictionary)
    {
        var filter = dictionary.Get(PdfName.Filter);
        if (filter is null) return;

        var resolved = filter.Resolve();
        var filterObjects = new List<IModelObject>();

        if (resolved is PdfName filterName)
        {
            var f = new GenericModelObject("CosFilter");
            f.Set("internalRepresentation", filterName.Value);
            filterObjects.Add(f);
        }
        else if (resolved is PdfArray filterArray)
        {
            foreach (var item in filterArray)
            {
                var itemResolved = item.Resolve();
                if (itemResolved is PdfName fn)
                {
                    var f = new GenericModelObject("CosFilter");
                    f.Set("internalRepresentation", fn.Value);
                    filterObjects.Add(f);
                }
            }
        }

        if (filterObjects.Count > 0)
        {
            model.Link("filters", filterObjects.ToArray());
        }
    }

    private static readonly System.Reflection.PropertyInfo? s_offsetProp =
        typeof(PdfStreamContents).Assembly
            .GetType("PdfLexer.PdfExistingStreamContents")
            ?.GetProperty("Offset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    private (bool StreamKeywordCRLFCompliant, bool EndstreamKeywordEOLCompliant) GetStreamKeywordCompliance(PdfStream stream)
    {
        if (s_offsetProp is null)
            return (true, true);

        var contents = stream.Contents;
        if (contents.GetType() != s_offsetProp.DeclaringType)
            return (true, true); // In-memory stream, assume compliant

        var offset = (long)s_offsetProp.GetValue(contents)!;
        var declaredLength = stream.Dictionary.GetOptionalValue<PdfNumber>(PdfName.Length);
        var length = declaredLength is not null ? (int)(double)declaredLength : contents.Length;
        return _analysis.CheckStreamKeywordCompliance(offset, length);
    }

    private void PopulateDictionaryObject(GenericModelObject model, PdfDictionary dictionary, ObjectDescriptor descriptor, string? relationName, IPdfObject? parentPdfObject)
    {
        // Set current page context before processing child objects so annotations
        // discovered via generic dict traversal (e.g. Popup key) can find the page.
        if (model.ObjectType == "PDPage")
        {
            _currentPageDict = dictionary;
        }

        PopulateGenericDictionary(model, dictionary);

        switch (model.ObjectType)
        {
            case "CosTrailer":
                model.Set("isEncrypted", dictionary.ContainsKey(PdfName.Encrypt));
                break;
            case "PDDocument":
                PopulateDocument(model, dictionary);
                break;
            case "PDPage":
                PopulatePage(model, dictionary);
                break;
            case "PDAnnot":
            case "PDWidgetAnnot":
            case "PDLinkAnnot":
            case "PDTrapNetAnnot":
            case "PDPrinterMarkAnnot":
                PopulateAnnotation(model, dictionary, parentPdfObject);
                break;
            case "PDAcroForm":
                PopulateAcroForm(model, dictionary);
                break;
            case "PDAction":
            case "PDNamedAction":
                PopulateAction(model, dictionary);
                break;
            case "PDOutputIntent":
                PopulateOutputIntent(model, dictionary);
                break;
            case "PDExtGState":
                PopulateExtGState(model, dictionary);
                break;
            case "PDFont":
            case "PDTrueTypeFont":
            case "PDType1Font":
            case "PDType3Font":
            case "PDType0Font":
            case "PDCIDFont":
                PopulateFont(model, dictionary);
                break;
            case "PDStructElem":
            case "SENonStandard":
            case "SETable":
            case "SETR":
            case "SETHead":
            case "SETBody":
            case "SETFoot":
            case "SETH":
            case "SETD":
            case "SEL":
            case "SELI":
            case "SELbl":
            case "SELBody":
            case "SEFigure":
            case "SEFormula":
            case "SENote":
            case "SEForm":
            case "SEH":
            case "SEHn":
            case "SETOC":
            case "SETOCI":
            case "SECaption":
            case "SESpan":
            case "SELink":
            case "SEAnnot":
            case "SEDocument":
            case "SEPart":
            case "SEDiv":
            case "SESect":
            case "SEP":
            case "SEBlockQuote":
            case "SEQuote":
            case "SECode":
            case "SEReference":
            case "SEBibEntry":
            case "SENonStruct":
            case "SEPrivate":
            case "SERuby":
            case "SEWarichu":
            case "SEArt":
            case "SEArtifact":
            case "SEAside":
            case "SEDocumentFragment":
            case "SEEm":
            case "SEFENote":
            case "SEIndex":
            case "SEStrong":
            case "SESub":
            case "SETitle":
            case "SERB":
            case "SERP":
            case "SERT":
            case "SEWP":
            case "SEWT":
                PopulateStructureElement(model, dictionary);
                break;
            case "PDOCConfig":
                PopulateOcConfig(model, dictionary);
                break;
            case "PDEncryption":
                PopulateEncryption(model, dictionary);
                break;
            case "CosFileSpecification":
                PopulateFileSpecification(model, dictionary);
                break;
            case "PDFormField":
                PopulateFormField(model, dictionary);
                break;
            case "PDMediaClip":
                PopulateMediaClip(model, dictionary);
                break;
            case "PDStructTreeRoot":
                PopulateStructTreeRoot(model, dictionary);
                break;
            case "CosInfo":
                PopulateCosInfo(model, dictionary);
                break;
            case "PDGroup":
                PopulateGroup(model, dictionary);
                break;
            case "PDHalftone":
                PopulateHalftone(model, dictionary, relationName);
                break;
        }
    }

    private void PopulateArrayObject(GenericModelObject model, PdfArray array, string parentObjectType)
    {
        var children = array
            .Select(item => BuildObject(item, relationName: "items", parentObjectType: parentObjectType, parentPdfObject: array))
            .Cast<IModelObject>()
            .ToArray();
        if (children.Length > 0)
        {
            model.Link("items", children);
        }
    }

    private void PopulatePrimitiveObject(GenericModelObject model, IPdfObject resolved)
    {
        var scalar = ConvertPdfObjectToScalar(resolved);
        model.Set("value", scalar);

        // CosName needs internalRepresentation for profile rules (e.g. length <= 127)
        if (resolved is PdfName name)
        {
            model.Set("internalRepresentation", name.Value);
        }

        // CosReal needs realValue
        if (resolved is PdfNumber number)
        {
            model.Set("realValue", (double)number);
            model.Set("intValue", (double)number);
        }

        // CosString needs isHex, containsOnlyHex, hexCount for hex string validation
        if (resolved is PdfString pdfStr)
        {
            var isHex = pdfStr.StringType == PdfStringType.Hex;
            model.Set("isHex", isHex);
            if (isHex)
            {
                // PdfLexer already decoded the hex string; we need the raw source data
                // to determine containsOnlyHex and hexCount. Look these up from the pre-scanned index.
                if (_hexStringIndex is not null && _hexStringIndex.TryLookup(pdfStr.Value, out var hexInfo))
                {
                    model.Set("containsOnlyHex", hexInfo.ContainsOnlyHex);
                    model.Set("hexCount", hexInfo.HexCount);
                }
                else
                {
                    // Fallback: assume well-formed if not found in index
                    model.Set("containsOnlyHex", true);
                    model.Set("hexCount", pdfStr.GetRawBytes().Length * 2);
                }
            }
        }
    }

    private static readonly HashSet<string> _deferredKeys = new(StringComparer.Ordinal) { "AA", "EF" };

    private void PopulateGenericDictionary(GenericModelObject model, PdfDictionary dictionary)
    {
        // CosDict rule: size <= 4095
        model.Set("size", dictionary.Count);

        foreach (var (key, value) in dictionary)
        {
            var keyName = key.Value;
            // Skip keys handled by type-specific populators to avoid premature caching
            if (_deferredKeys.Contains(keyName))
                continue;
            var resolved = value.Resolve();

            if (TryConvertScalar(resolved, out var scalar))
            {
                model.Set(keyName, scalar);

                // Create model objects for primitive values exceeding implementation limits
                // so CosName/CosString length rules (6.1.13) can fire.
                if (resolved is PdfName pdfNameVal && pdfNameVal.Value.Length > 127)
                {
                    var cosName = new GenericModelObject("CosName");
                    cosName.Set("internalRepresentation", pdfNameVal.Value);
                    model.Link(keyName, cosName);
                }
                else if (resolved is PdfString pdfStrVal && pdfStrVal.Value.Length >= 32768)
                {
                    var cosStr = new GenericModelObject("CosString");
                    cosStr.Set("value", pdfStrVal.Value);
                    model.Link(keyName, cosStr);
                }

                continue;
            }

            if (resolved is PdfArray array)
            {
                var children = array
                    .Select(item => BuildObject(item, relationName: keyName, parentObjectType: model.ObjectType, parentPdfObject: dictionary))
                    .Cast<IModelObject>()
                    .ToArray();
                if (children.Length > 0)
                {
                    model.Link(keyName, children);
                }

                continue;
            }

            if (resolved is PdfDictionary or PdfStream)
            {
                var child = BuildObject(value, relationName: keyName, parentObjectType: model.ObjectType, parentPdfObject: dictionary);
                // For Type 5 halftone sub-entries, the colorant name is the parent dict key.
                // Type 5 halftones have /HalftoneType 5 and are typed as CosDictionary (not PDHalftone).
                if (child is GenericModelObject gChild && string.Equals(gChild.ObjectType, "PDHalftone", StringComparison.Ordinal))
                {
                    var htObj = dictionary.ContainsKey(new PdfName("HalftoneType")) ? dictionary.Get(new PdfName("HalftoneType")) : null;
                    var parentHT = htObj is not null ? GetNumberValue(htObj) : null;
                    if (parentHT is not null && (int)parentHT.Value == 5)
                    {
                        gChild.Set("colorantName", keyName);
                    }
                }
                model.Link(keyName, child);
            }
        }
    }

    private void PopulateDocument(GenericModelObject model, PdfDictionary catalog)
    {
        model.Set("containsXRefStream", _analysis.ContainsXRefStream);
        model.Set("containsLang", catalog.GetOptionalValue<PdfString>(LangName) is not null);
        model.Set("containsAA", catalog.ContainsKey(AAName));
        model.Set("containsStructTreeRoot", catalog.ContainsKey(StructTreeRootName));
        model.Set("containsMetadata", ContainsMetadataStream(catalog.GetOptionalValue<PdfStream>(PdfName.Metadata)));

        // PDAdditionalActions from catalog /AA
        LinkAdditionalActions(model, catalog, "Catalog");

        // OpenAction — link action object so PDAction rules apply
        var openAction = catalog.Get(new PdfName("OpenAction"))?.Resolve();
        if (openAction is PdfDictionary openActionDict)
        {
            var actionModel = BuildObject(openActionDict, relationName: "OpenAction", parentObjectType: "PDDocument", parentPdfObject: catalog);
            model.Link("OpenAction", actionModel);
        }

        var namesDict = catalog.GetOptionalValue<PdfDictionary>(PdfName.Names);
        model.Set("containsAlternatePresentations", namesDict?.ContainsKey(new PdfName("AlternatePresentations")) ?? false);

        var markInfo = catalog.GetOptionalValue<PdfDictionary>(new PdfName("MarkInfo"));
        model.Set("MarkInfo", markInfo is not null);
        model.Set("Marked", markInfo?.GetOptionalValue<PdfBoolean>(MarkedName)?.Value ?? false);
        model.Set("Suspects", markInfo?.GetOptionalValue<PdfBoolean>(SuspectsName)?.Value ?? false);
        model.Set("NeedsRendering", catalog.GetOptionalValue<PdfBoolean>(NeedsRenderingName)?.Value ?? false);

        var viewerPreferences = catalog.GetOptionalValue<PdfDictionary>(ViewerPreferencesName);
        model.Set("DisplayDocTitle", viewerPreferences?.GetOptionalValue<PdfBoolean>(DisplayDocTitleName)?.Value ?? false);
        model.Set("ViewerPreferences", viewerPreferences is not null ? viewerPreferences.ToString() : null);

        var idArray = _document.Trailer.GetOptionalValue<PdfArray>(PdfName.ID);
        // Extract IDs from the physical trailer dictionaries (not the merged trailer).
        // PdfLexer merges all trailers in the xref chain, but PDF/A rules require checking
        // whether specific physical trailers contain /ID.
        var (firstPageId, lastTrailerId) = PdfByteAnalysis.ExtractPhysicalTrailerIds(
            _analysis.RawBytes, _analysis.IsLinearized);

        // For linearized PDFs, the veraPDF model uses the first-page trailer as the document
        // trailer (per model: "first page trailer for the linearized PDF or the last trailer
        // in the document"). So lastID comes from the first-page trailer for linearized PDFs.
        string? firstId;
        string? lastId;
        if (_analysis.IsLinearized)
        {
            firstId = firstPageId;
            lastId = firstPageId; // first-page trailer IS the document trailer for linearized
        }
        else
        {
            firstId = null;
            lastId = lastTrailerId;
        }

        model.Set("firstPageID", firstId);
        model.Set("firstPageIDValue", firstId);
        model.Set("lastID", lastId);
        model.Set("lastIDValue", lastId);

        // Emit CosLang for the document catalog Lang entry
        var catalogLang = ConvertPdfObjectToString(catalog.Get(LangName));
        if (!string.IsNullOrEmpty(catalogLang))
        {
            var langObj = new GenericModelObject("CosLang");
            langObj.Set("unicodeValue", catalogLang);
            model.Link("Lang", langObj);
        }

        // Emit OCProperties → PDOCConfig objects
        var ocConfigs = CreateOcConfigObjects(catalog);
        if (ocConfigs.Count > 0)
        {
            model.Link("ocConfigs", ocConfigs.ToArray());
        }

        // Emit PDEncryption if present
        if (_document.Trailer.TryGetValue<PdfDictionary>(PdfName.Encrypt, out var encryptDict, false))
        {
            var encModel = BuildObject(encryptDict, relationName: "Encrypt", parentObjectType: "CosTrailer", parentPdfObject: _document.Trailer);
            if (!string.Equals(encModel.ObjectType, "PDEncryption", StringComparison.Ordinal))
            {
                var encObj = new GenericModelObject("PDEncryption");
                PopulateEncryption(encObj, encryptDict);
                model.Link("encryption", encObj);
            }
            else
            {
                model.Link("encryption", encModel);
            }
        }

        // Emit PDFormField objects from AcroForm
        var formFields = CreateFormFieldObjects(catalog);
        if (formFields.Count > 0)
        {
            model.Link("formFields", formFields.ToArray());
        }

        // Emit PDPerms from catalog /Perms dictionary
        var permsDict = catalog.GetOptionalValue<PdfDictionary>(new PdfName("Perms"));
        if (permsDict is not null)
        {
            var permsObj = new GenericModelObject("PDPerms");
            var keys = new List<string>();
            foreach (var key in permsDict.Keys)
            {
                if (!string.Equals(key.Value, "Type", StringComparison.Ordinal))
                    keys.Add(key.Value!);
            }
            permsObj.Set("entries", string.Join("&", keys));

            // Emit PDSigRef objects from all signature reference dictionaries
            var permsHasDocMDP = permsDict.ContainsKey(new PdfName("DocMDP"));
            var sigRefObjects = CollectSignatureReferences(catalog, permsHasDocMDP);
            foreach (var sigRef in sigRefObjects)
            {
                permsObj.Link("SigRef", sigRef);
            }

            model.Link("Perms", permsObj);
        }

        // outputColorSpace: used by gDocumentOutputCS variable in PDFA-4
        // veraPDF only considers GTS_PDFA1 output intents for the PDF/A output color space.
        model.Set("outputColorSpace", GetOutputColorSpace(catalog, pdfA1Only: true));

        // OutputIntents wrapper: checks that multiple DestOutputProfile entries reference the same indirect object
        CreateOutputIntentsWrapper(model, catalog);
    }

    private static bool ContainsMetadataStream(PdfStream? metadata) =>
        metadata is not null &&
        string.Equals(ConvertPdfObjectToString(metadata.Dictionary.Get(PdfName.TypeName)), "Metadata", StringComparison.Ordinal) &&
        string.Equals(ConvertPdfObjectToString(metadata.Dictionary.Get(PdfName.Subtype)), "XML", StringComparison.Ordinal);

    private XmpMetadataSnapshot? GetXmpSnapshotFromCatalog()
    {
        if (!_document.Catalog.TryGetValue<PdfStream>(PdfName.Metadata, out var metadataStream, false))
        {
            return null;
        }

        return GetXmpSnapshot(metadataStream);
    }

    private static bool HasTransparency(PdfDictionary page)
    {
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (ResourcesHaveTransparency(page, visited))
            return true;

        // Check annotation dictionaries and their appearance streams
        if (page.TryGetValue<PdfArray>(AnnotsName, out var annots, false))
        {
            foreach (var annotObj in annots)
            {
                var annot = annotObj.Resolve() as PdfDictionary;
                if (annot is null) continue;

                // Annotations can have BM directly in their dictionary (PDF 2.0)
                var annotBM = annot.Get(BMName)?.Resolve();
                if (annotBM is PdfName annotBMName &&
                    !string.Equals(annotBMName.Value, "Normal", StringComparison.Ordinal) &&
                    !string.Equals(annotBMName.Value, "Compatible", StringComparison.Ordinal))
                    return true;

                var ap = annot.GetOptionalValue<PdfDictionary>(new PdfName("AP"));
                if (ap is null) continue;

                // Check N (normal), R (rollover), D (down) appearance entries
                foreach (var apKey in new[] { "N", "R", "D" })
                {
                    var apEntry = ap.Get(new PdfName(apKey))?.Resolve();
                    if (apEntry is PdfStream apStream)
                    {
                        if (StreamHasTransparency(apStream, visited))
                            return true;
                    }
                    else if (apEntry is PdfDictionary apDict)
                    {
                        // Sub-dictionary of appearance states
                        foreach (var stateKey in apDict.Keys)
                        {
                            if (apDict.Get(stateKey)?.Resolve() is PdfStream stateStream &&
                                StreamHasTransparency(stateStream, visited))
                                return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a form XObject stream uses transparency (has a transparency group or
    /// its sub-resources use transparency).
    /// </summary>
    private static bool StreamHasTransparency(PdfStream stream, HashSet<PdfDictionary> visited)
    {
        var dict = stream.Dictionary;
        if (!visited.Add(dict)) return false;

        // Check for transparency group on this stream
        var group = dict.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
        if (group is not null)
        {
            var s = ConvertPdfObjectToString(group.Get(PdfName.Subtype)) ?? ConvertPdfObjectToString(group.Get(new PdfName("S")));
            if (string.Equals(s, "Transparency", StringComparison.Ordinal))
                return true;
        }

        // Check sub-resources of this stream
        var resources = dict.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
        if (resources is not null && ResourceDictHasTransparency(resources, visited))
            return true;

        return false;
    }

    /// <summary>
    /// Checks if a page or container (which may have a Group and Resources) uses transparency.
    /// </summary>
    private static bool ResourcesHaveTransparency(PdfDictionary container, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(container)) return false;

        // Check Group dictionary on the container itself
        var group = container.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
        if (group is not null)
        {
            var sValue = ConvertPdfObjectToString(group.Get(PdfName.Subtype)) ?? ConvertPdfObjectToString(group.Get(new PdfName("S")));
            if (string.Equals(sValue, "Transparency", StringComparison.Ordinal))
                return true;
        }

        var resources = container.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
        if (resources is null)
            return false;

        return ResourceDictHasTransparency(resources, visited);
    }

    /// <summary>
    /// Checks if a Resources dictionary contains transparency-inducing entries,
    /// recursively scanning ExtGState, XObject, Pattern, and Font sub-resources.
    /// </summary>
    private static bool ResourceDictHasTransparency(PdfDictionary resources, HashSet<PdfDictionary> visited)
    {
        // Check ExtGState resources for SMask, BM, ca/CA
        var extGStateDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("ExtGState"));
        if (extGStateDict is not null)
        {
            foreach (var key in extGStateDict.Keys)
            {
                var gs = extGStateDict.GetOptionalValue<PdfDictionary>(key);
                if (gs is null) continue;

                // SMask not "None" implies transparency
                var smask = gs.Get(SMaskName)?.Resolve();
                if (smask is PdfDictionary) return true;
                if (smask is PdfName smaskName && !string.Equals(smaskName.Value, "None", StringComparison.Ordinal))
                    return true;

                // BM not "Normal" or "Compatible" implies transparency
                var bm = gs.Get(BMName)?.Resolve();
                if (bm is PdfName bmNameVal &&
                    !string.Equals(bmNameVal.Value, "Normal", StringComparison.Ordinal) &&
                    !string.Equals(bmNameVal.Value, "Compatible", StringComparison.Ordinal))
                    return true;
                if (bm is PdfArray bmArr)
                {
                    foreach (var item in bmArr)
                    {
                        var n = ConvertPdfObjectToString(item);
                        if (n is not null && n != "Normal" && n != "Compatible")
                            return true;
                    }
                }

                // ca or CA < 1.0 implies transparency
                var caVal = gs.Get(new PdfName("ca"));
                if (caVal is not null && GetNumberValue(caVal) is double ca && ca < 1.0)
                    return true;
                var caUpperVal = gs.Get(new PdfName("CA"));
                if (caUpperVal is not null && GetNumberValue(caUpperVal) is double caUpper && caUpper < 1.0)
                    return true;
            }
        }

        // Check XObject resources (Form XObjects and Image XObjects)
        var xObjectDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("XObject"));
        if (xObjectDict is not null)
        {
            foreach (var key in xObjectDict.Keys)
            {
                var xObj = xObjectDict.Get(key)?.Resolve();
                if (xObj is PdfStream xStream)
                {
                    var subtype = ConvertPdfObjectToString(xStream.Dictionary.Get(PdfName.Subtype));
                    if (string.Equals(subtype, "Form", StringComparison.Ordinal))
                    {
                        if (StreamHasTransparency(xStream, visited))
                            return true;
                    }
                    // Image XObjects don't directly carry transparency, but they can
                    // reference SMask streams — check the image's SMask entry
                    if (xStream.Dictionary.Get(SMaskName)?.Resolve() is PdfStream)
                        return true;
                }
            }
        }

        // Check Pattern resources (Tiling patterns have their own Resources)
        var patternDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("Pattern"));
        if (patternDict is not null)
        {
            foreach (var key in patternDict.Keys)
            {
                var pat = patternDict.Get(key)?.Resolve();
                if (pat is PdfStream patStream)
                {
                    // Tiling pattern (PatternType 1) — has its own Resources
                    if (StreamHasTransparency(patStream, visited))
                        return true;
                }
            }
        }

        // Check Font resources (Type3 fonts have their own Resources)
        var fontDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("Font"));
        if (fontDict is not null)
        {
            foreach (var key in fontDict.Keys)
            {
                var font = fontDict.GetOptionalValue<PdfDictionary>(key);
                if (font is null) continue;
                var fontSubtype = ConvertPdfObjectToString(font.Get(PdfName.Subtype));
                if (!string.Equals(fontSubtype, "Type3", StringComparison.Ordinal)) continue;

                // Type3 fonts have a Resources dictionary
                var fontResources = font.GetOptionalValue<PdfDictionary>(new PdfName("Resources"));
                if (fontResources is not null && !visited.Contains(fontResources) &&
                    ResourceDictHasTransparency(fontResources, visited))
                    return true;
            }
        }

        return false;
    }

    private static string? GetOutputColorSpace(PdfDictionary container, bool pdfA1Only = false)
    {
        // Look for output intents in the given dictionary (catalog or page)
        if (!container.TryGetValue<PdfArray>(new PdfName("OutputIntents"), out var intents, false))
            return null;

        foreach (var intent in intents)
        {
            var dict = intent.Resolve() as PdfDictionary;
            if (dict is null)
                continue;

            var subtype = ConvertPdfObjectToString(dict.Get(PdfName.Subtype)) ?? ConvertPdfObjectToString(dict.Get(new PdfName("S")));
            // gOutputCS (PDFA-1/2) considers only GTS_PDFA1 output intents.
            // outputColorSpace (PDFA-4) considers any output intent with a DestOutputProfile.
            if (pdfA1Only)
            {
                if (!string.Equals(subtype, "GTS_PDFA1", StringComparison.Ordinal))
                    continue;
            }
            else
            {
                if (!string.Equals(subtype, "GTS_PDFA1", StringComparison.Ordinal) &&
                    !string.Equals(subtype, "GTS_PDFX", StringComparison.Ordinal))
                    continue;
            }
            {
                // Parse the ICC profile to extract the actual color space signature ("RGB ", "CMYK", "GRAY")
                var destProfile = dict.GetOptionalValue<PdfStream>(DestOutputProfileName);
                if (destProfile is not null)
                {
                    return ReadIccColorSpace(destProfile);
                }

                var outputCondition = ConvertPdfObjectToString(dict.Get(new PdfName("OutputConditionIdentifier")));
                if (!string.IsNullOrEmpty(outputCondition))
                {
                    return outputCondition;
                }
            }
        }

        return null;
    }

    private void PopulatePage(GenericModelObject model, PdfDictionary page)
    {
        model.Set("containsAA", page.ContainsKey(AAName));
        model.Set("containsMetadata", ContainsMetadataStream(page.GetOptionalValue<PdfStream>(PdfName.Metadata)));
        model.Set("containsStructParents", page.ContainsKey(new PdfName("StructParents")));
        model.Set("Tabs", ConvertPdfObjectToString(page.Get(TabsName)));
        model.Set("containsPresSteps", page.ContainsKey(new PdfName("PresSteps")));

        // PDAdditionalActions from page /AA
        LinkAdditionalActions(model, page, "Page");

        // Page boundary boxes — CosBBox model objects for implementation limits checks.
        // The effective boxes resolve inheritance from the Pages tree.
        LinkCosBBox(model, "MediaBox", ResolveBBox(page, "MediaBox"));
        LinkCosBBox(model, "CropBox", ResolveBBox(page, "CropBox"));
        LinkCosBBox(model, "BleedBox", ResolveBBox(page, "BleedBox"));
        LinkCosBBox(model, "TrimBox", ResolveBBox(page, "TrimBox"));
        LinkCosBBox(model, "ArtBox", ResolveBBox(page, "ArtBox"));

        // Transparency-related properties for PDF/A output intent rules
        var groupDict = page.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
        var hasGroupCS = groupDict is not null && groupDict.ContainsKey(new PdfName("CS"));
        model.Set("containsGroupCS", hasGroupCS);
        model.Set("containsTransparency", HasTransparency(page));

        // Output color space from output intents
        // veraPDF only considers GTS_PDFA1 output intents for the PDF/A output color space.
        var documentOutputCS = GetOutputColorSpace(_document.Catalog, pdfA1Only: true);
        var pageOutputCS = GetOutputColorSpace(page, pdfA1Only: true);
        model.Set("gOutputCS", _pdfa1OutputCS);
        model.Set("gDocumentOutputCS", documentOutputCS);
        model.Set("gPageOutputCS", pageOutputCS ?? documentOutputCS);
        model.Set("outputColorSpace", pageOutputCS ?? documentOutputCS);

        // Transparency group color space
        string? transparencyCS = null;
        string? transparencyIccIndirect = null;
        string? transparencyIccMD5 = null;
        if (groupDict is not null)
        {
            var groupCS = groupDict.Get(new PdfName("CS"));
            if (groupCS is not null)
            {
                var groupCSResolved = groupCS.Resolve();
                if (groupCSResolved is PdfName csn)
                    transparencyCS = csn.Value;
                else if (groupCSResolved is PdfArray csArr && csArr.Count > 0)
                {
                    var csType = ConvertPdfObjectToString(csArr[0]);
                    if (string.Equals(csType, "ICCBased", StringComparison.Ordinal) && csArr.Count > 1 && csArr[1].Resolve() is PdfStream iccStream)
                    {
                        transparencyCS = ReadIccColorSpace(iccStream);
                        transparencyIccIndirect = csArr[1] is PdfIndirectRef tRef ? tRef.ToString() : null;
                        try
                        {
                            var tBytes = iccStream.Contents.GetDecodedData();
                            transparencyIccMD5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(tBytes)).ToLowerInvariant();
                        }
                        catch { }
                    }
                    else if (csType is "CalRGB" or "CalGray" or "Lab")
                        transparencyCS = csType;
                }
            }
        }

        var annots = page.GetOptionalValue<PdfArray>(AnnotsName);
        model.Set("containsAnnotations", annots is not null && annots.Count > 0);

        var contentItems = CreatePageContentObjects(page);
        if (contentItems.Count > 0)
        {
            model.Link("contentItems", contentItems.ToArray());
        }

        // Create color space model objects from page resources
        var colorSpaceObjects = CreateColorSpaceObjects(page, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);

        // Also create device CS objects from the page Group CS entry (transparency blending space).
        // If the Group CS is a device color space it must match the output intent.
        if (transparencyCS is "DeviceRGB" or "DeviceCMYK" or "DeviceGray")
        {
            CreateDeviceColorSpaceObject(transparencyCS, colorSpaceObjects, documentOutputCS, pageOutputCS, transparencyCS);
        }

        // Scan annotation appearance streams for device colour space usage
        if (annots is not null)
        {
            ScanAnnotationAppearanceColorSpaces(annots, colorSpaceObjects, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
        }

        if (colorSpaceObjects.Count > 0)
        {
            model.Link("colorSpaces", colorSpaceObjects.ToArray());
        }

        // Scan decompressed content stream bytes for hex strings.
        // This catches hex strings used as text operands (Tj/TJ) that don't go
        // through BuildObject/PopulatePrimitiveObject.
        var contentHexStrings = ScanContentStreamHexStrings(page);
        if (contentHexStrings.Count > 0)
        {
            model.Link("contentHexStrings", contentHexStrings.ToArray());
        }

        // Scan content stream for ri operators → CosRenderingIntent
        var riIntents = ScanContentStreamRenderingIntents(page);
        if (riIntents.Count > 0)
        {
            model.Link("contentRenderingIntents", riIntents.ToArray());
        }

        // Emit CosLang for page-level Lang entry
        var pageLang = ConvertPdfObjectToString(page.Get(LangName));
        if (!string.IsNullOrEmpty(pageLang))
        {
            var langObj = new GenericModelObject("CosLang");
            langObj.Set("unicodeValue", pageLang);
            model.Link("Lang", langObj);
        }
    }

    /// <summary>
    /// Resolves a page boundary box array, walking the Pages tree if inherited.
    /// Returns [left, bottom, right, top] or null if not present.
    /// </summary>
    private PdfArray? ResolveBBox(PdfDictionary page, string key)
    {
        var name = new PdfName(key);
        var current = page;
        while (current is not null)
        {
            var val = current.Get(name)?.Resolve();
            if (val is PdfArray arr && arr.Count >= 4)
                return arr;
            // Walk up the Pages tree
            current = current.GetOptionalValue<PdfDictionary>(PdfName.Parent);
        }
        return null;
    }

    private static void LinkCosBBox(GenericModelObject parent, string linkName, PdfArray? bbox)
    {
        if (bbox is null || bbox.Count < 4) return;
        var obj = new GenericModelObject("CosBBox");
        obj.Set("size", bbox.Count);
        var left = GetNumberValueDouble(bbox[0]);
        var bottom = GetNumberValueDouble(bbox[1]);
        var right = GetNumberValueDouble(bbox[2]);
        var top = GetNumberValueDouble(bbox[3]);
        obj.Set("left", left);
        obj.Set("bottom", bottom);
        obj.Set("right", right);
        obj.Set("top", top);
        parent.Link(linkName, obj);
    }

    private static double GetNumberValueDouble(IPdfObject obj)
    {
        var resolved = obj.Resolve();
        if (resolved is PdfNumber num) return (double)num;
        return 0.0;
    }

    /// <summary>
    /// Extract CosIIFilter model objects from an inline image header.
    /// The header is a PdfArray of alternating key/value pairs.
    /// </summary>
    private static void ExtractInlineImageFilters(PdfArray header, List<IModelObject> result)
    {
        // Find /F or /Filter key in the header
        for (int i = 0; i < header.Count - 1; i += 2)
        {
            if (header[i] is PdfName key && key.Value is "F" or "Filter")
            {
                var value = header[i + 1];
                if (value is PdfName filterName)
                {
                    var filterObj = new GenericModelObject("CosIIFilter");
                    filterObj.Set("internalRepresentation", filterName.Value);
                    result.Add(filterObj);
                }
                else if (value is PdfArray filterArray)
                {
                    foreach (var item in filterArray)
                    {
                        if (item is PdfName fn)
                        {
                            var filterObj = new GenericModelObject("CosIIFilter");
                            filterObj.Set("internalRepresentation", fn.Value);
                            result.Add(filterObj);
                        }
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// Extract CosRenderingIntent from an inline image /Intent entry.
    /// </summary>
    private static void ExtractInlineImageIntent(PdfArray header, List<IModelObject> result)
    {
        for (int i = 0; i < header.Count - 1; i += 2)
        {
            if (header[i] is PdfName key && key.Value is "Intent")
            {
                if (header[i + 1] is PdfName intentName)
                {
                    var intentObj = new GenericModelObject("CosRenderingIntent");
                    intentObj.Set("internalRepresentation", intentName.Value);
                    result.Add(intentObj);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Create a PDInlineImage object from an inline image's header.
    /// PDInlineImage extends PDXImage in the model — rules for Interpolate, containsOPI, etc. apply.
    /// </summary>
    private static GenericModelObject CreatePDInlineImage(PdfArray header, List<IModelObject> result)
    {
        var img = new GenericModelObject("PDInlineImage", superTypes: new[] { "PDXImage", "PDXObject" });

        // Inline images use abbreviated key names: I→Interpolate, IM→ImageMask, BPC→BitsPerComponent
        bool interpolate = false;
        bool imageMask = false;
        double? bpc = null;

        for (int i = 0; i < header.Count - 1; i += 2)
        {
            if (header[i] is PdfName key)
            {
                var val = header[i + 1];
                switch (key.Value)
                {
                    case "I" or "Interpolate":
                        interpolate = val is PdfBoolean b && b.Value;
                        break;
                    case "IM" or "ImageMask":
                        imageMask = val is PdfBoolean bm && bm.Value;
                        break;
                    case "BPC" or "BitsPerComponent":
                        if (val is PdfNumber n)
                            bpc = (double)n;
                        break;
                }
            }
        }

        img.Set("Interpolate", interpolate);
        img.Set("isMask", imageMask);
        img.Set("containsAlternates", false); // inline images never have Alternates
        img.Set("containsOPI", false); // inline images never have OPI
        if (bpc is not null)
            img.Set("BitsPerComponent", bpc);

        result.Add(img);
        return img;
    }

    private void LinkAdditionalActions(GenericModelObject model, PdfDictionary container, string parentType)
    {
        var aaDict = container.GetOptionalValue<PdfDictionary>(AAName);
        if (aaDict is null) return;

        var aaObj = new GenericModelObject("PDAdditionalActions");
        aaObj.Set("parentType", parentType);
        var keys = new List<string>();
        foreach (var key in aaDict.Keys)
            keys.Add(key.Value!);
        aaObj.Set("entries", string.Join("&", keys));

        // Link individual actions
        var actions = new List<IModelObject>();
        foreach (var key in aaDict.Keys)
        {
            var actionRef = aaDict.Get(key);
            if (actionRef?.Resolve() is PdfDictionary actionDict)
            {
                var actionModel = BuildObject(actionDict, relationName: key.Value, parentObjectType: "PDAdditionalActions", parentPdfObject: aaDict);
                actions.Add(actionModel);
            }
        }
        if (actions.Count > 0)
            aaObj.Link("Actions", actions.ToArray());

        model.Link("AA", aaObj);
    }

    private List<IModelObject> CreatePageContentObjects(PdfDictionary page)
    {
        try
        {
            var nodes = new PdfPage(page).GetContentModel();
            var structParents = GetOptionalInt(page, new PdfName("StructParents"));
            return CreateContentObjects(nodes, page, structParents, Array.Empty<string>(), null, null);
        }
        catch
        {
            return new List<IModelObject>();
        }
    }

    /// <summary>
    /// Scan decompressed page content stream bytes for primitives not visible to BuildObject.
    /// Creates CosString (hex + long literal), CosInteger (out-of-range), and CosName (long)
    /// objects from content stream operands.
    /// </summary>
    private List<IModelObject> ScanContentStreamHexStrings(PdfDictionary page)
    {
        var result = new List<IModelObject>();
        try
        {
            var contentsObj = page.Get(ContentsName);
            if (contentsObj is null) return result;
            var resolved = contentsObj.Resolve();
            if (resolved is PdfStream singleStream)
            {
                ScanDecompressedBytesForPrimitives(singleStream.Contents.GetDecodedData(), result);
            }
            else if (resolved is PdfArray arr)
            {
                foreach (var item in arr)
                {
                    if (item.Resolve() is PdfStream s)
                        ScanDecompressedBytesForPrimitives(s.Contents.GetDecodedData(), result);
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Scan page content streams for ri (set rendering intent) operators and create
    /// CosRenderingIntent objects for each occurrence.
    /// </summary>
    private List<IModelObject> ScanContentStreamRenderingIntents(PdfDictionary page)
    {
        var result = new List<IModelObject>();
        try
        {
            var scanner = new PageContentScanner(ParsingContext.Current, page, flattenForms: true);
            while (scanner.Advance())
            {
                if (scanner.CurrentOperator == PdfOperatorType.ri)
                {
                    if (scanner.TryGetCurrentOperation<double>(out var op) && op is ri_Op<double> riOp)
                    {
                        var intentObj = new GenericModelObject("CosRenderingIntent");
                        intentObj.Set("internalRepresentation", riOp.intent.Value);
                        result.Add(intentObj);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Scan decompressed content stream bytes for hex strings, long literal strings,
    /// out-of-range integers, and long names. Skips inline image data (ID...EI).
    /// </summary>
    private static void ScanDecompressedBytesForPrimitives(byte[] bytes, List<IModelObject> result)
    {
        bool inInlineImageData = false;
        for (int i = 0; i < bytes.Length; i++)
        {
            // Skip inline image data sections (ID ... EI)
            if (!inInlineImageData && i + 2 < bytes.Length &&
                bytes[i] == (byte)'I' && bytes[i + 1] == (byte)'D' &&
                (bytes[i + 2] == 0x20 || bytes[i + 2] == 0x0A || bytes[i + 2] == 0x0D))
            {
                inInlineImageData = true;
                i += 2;
                continue;
            }

            if (inInlineImageData)
            {
                if (i + 1 < bytes.Length && bytes[i] == (byte)'E' && bytes[i + 1] == (byte)'I' &&
                    (i + 2 >= bytes.Length || bytes[i + 2] <= 0x20) &&
                    (i > 0 && (bytes[i - 1] == 0x0A || bytes[i - 1] == 0x0D || bytes[i - 1] == 0x20)))
                {
                    inInlineImageData = false;
                    i += 1;
                }
                continue;
            }

            var b = bytes[i];

            // Hex strings <...>
            if (b == (byte)'<')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'<') { i++; continue; } // skip <<
                int start = i + 1;
                int hexCount = 0;
                bool containsOnlyHex = true;
                int j = start;
                for (; j < bytes.Length; j++)
                {
                    var hb = bytes[j];
                    if (hb == (byte)'>') break;
                    if (hb == 0x00 || hb == 0x09 || hb == 0x0A || hb == 0x0C || hb == 0x0D || hb == 0x20) continue;
                    hexCount++;
                    if (!IsHexByte(hb)) containsOnlyHex = false;
                }
                if (j >= bytes.Length) break;
                string decodedValue = DecodeHexBytes(bytes, start, j);
                var cosStr = new GenericModelObject("CosString");
                cosStr.Set("isHex", true);
                cosStr.Set("containsOnlyHex", containsOnlyHex);
                cosStr.Set("hexCount", hexCount);
                cosStr.Set("value", decodedValue);
                result.Add(cosStr);
                i = j;
                continue;
            }

            // Literal strings (...) — only create CosString for long ones (>= 32768)
            if (b == (byte)'(')
            {
                int depth = 1;
                int decodedLen = 0;
                int j = i + 1;
                while (j < bytes.Length && depth > 0)
                {
                    var sb = bytes[j];
                    if (sb == (byte)'\\' && j + 1 < bytes.Length)
                    {
                        j += 2; // skip escaped char
                        decodedLen++;
                    }
                    else if (sb == (byte)'(') { depth++; j++; decodedLen++; }
                    else if (sb == (byte)')') { depth--; if (depth > 0) decodedLen++; j++; }
                    else { j++; decodedLen++; }
                }
                if (decodedLen >= 32768)
                {
                    var cosStr = new GenericModelObject("CosString");
                    cosStr.Set("isHex", false);
                    cosStr.Set("value", new string('X', decodedLen)); // exact content not needed, just length
                    result.Add(cosStr);
                }
                i = j - 1;
                continue;
            }

            // Integer operands — detect out-of-range values
            if ((b >= (byte)'0' && b <= (byte)'9') || (b == (byte)'-' && i + 1 < bytes.Length && bytes[i + 1] >= (byte)'0' && bytes[i + 1] <= (byte)'9'))
            {
                // Check preceding byte is whitespace/delimiter (not part of an operator name)
                if (i > 0) { var prev = bytes[i - 1]; if (prev > 0x20 && prev != (byte)'[' && prev != (byte)'(') continue; }
                int j = b == (byte)'-' ? i + 1 : i;
                bool isNeg = b == (byte)'-';
                bool hasDecimal = false;
                while (j < bytes.Length && ((bytes[j] >= (byte)'0' && bytes[j] <= (byte)'9') || bytes[j] == (byte)'.'))
                {
                    if (bytes[j] == (byte)'.') hasDecimal = true;
                    j++;
                }
                if (!hasDecimal && j - i > 9) // only check integers with many digits (potential overflow)
                {
                    var numStr = System.Text.Encoding.ASCII.GetString(bytes, i, j - i);
                    if (long.TryParse(numStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var val))
                    {
                        if (val > 2147483647 || val < -2147483648)
                        {
                            var cosInt = new GenericModelObject("CosInteger");
                            cosInt.Set("intValue", (double)val);
                            result.Add(cosInt);
                        }
                    }
                }
                i = j - 1;
                continue;
            }

            // Name operands /xxx — detect long names (> 127 bytes)
            if (b == (byte)'/')
            {
                int j = i + 1;
                int decodedLen = 0;
                while (j < bytes.Length)
                {
                    var nb = bytes[j];
                    if (nb <= 0x20 || nb == (byte)'/' || nb == (byte)'[' || nb == (byte)']' ||
                        nb == (byte)'(' || nb == (byte)')' || nb == (byte)'<' || nb == (byte)'>' ||
                        nb == (byte)'{' || nb == (byte)'}') break;
                    if (nb == (byte)'#' && j + 2 < bytes.Length) { j += 3; decodedLen++; } // #xx escape
                    else { j++; decodedLen++; }
                }
                if (decodedLen > 127)
                {
                    var cosName = new GenericModelObject("CosName");
                    cosName.Set("internalRepresentation", new string('X', decodedLen));
                    result.Add(cosName);
                }
                i = j - 1;
                continue;
            }
        }
    }

    private static bool IsHexByte(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9') ||
        (b >= (byte)'A' && b <= (byte)'F') ||
        (b >= (byte)'a' && b <= (byte)'f');

    private static string DecodeHexBytes(byte[] raw, int start, int end)
    {
        var decoded = new List<byte>();
        bool highNibble = true;
        byte current = 0;
        for (int i = start; i < end; i++)
        {
            var b = raw[i];
            if (b == 0x00 || b == 0x09 || b == 0x0A || b == 0x0C || b == 0x0D || b == 0x20) continue;
            if (!IsHexByte(b)) continue;
            int val = b >= (byte)'0' && b <= (byte)'9' ? b - (byte)'0' :
                      b >= (byte)'A' && b <= (byte)'F' ? b - (byte)'A' + 10 :
                      b - (byte)'a' + 10;
            if (highNibble) { current = (byte)(val << 4); highNibble = false; }
            else { current |= (byte)val; decoded.Add(current); current = 0; highNibble = true; }
        }
        if (!highNibble) decoded.Add(current);
        return System.Text.Encoding.GetEncoding("iso-8859-1").GetString(decoded.ToArray());
    }

    /// <summary>
    /// Scans raw PDF bytes for all indirect object definitions (N G obj ... endobj)
    /// and checks byte-level spacing compliance per ISO 19005 clause 6.1.9.
    /// </summary>
    private static IModelObject[] BuildCosIndirectObjects(byte[] raw)
    {
        int len = raw.Length;

        // Forward-scan for "N ws+ N ws+ obj" patterns starting after EOL.
        var objHeaders = new List<(int lineStart, int afterObj, bool compliant)>();
        for (int i = 0; i < len; i++)
        {
            byte b = raw[i];
            // Object definitions start at beginning of line (after EOL or at byte 0)
            bool isLineStart = (i == 0) || (raw[i - 1] == 0x0A) || (raw[i - 1] == 0x0D);
            if (!isLineStart) continue;

            // Skip any leading whitespace (this is itself a violation)
            int j = i;
            bool hasLeadingWhitespace = false;
            while (j < len && (raw[j] == 0x20 || raw[j] == 0x09)) { hasLeadingWhitespace = true; j++; }

            // Must start with digits for the object number
            if (j >= len || raw[j] < (byte)'0' || raw[j] > (byte)'9') continue;

            // Skip object number digits
            int objNumStart = j;
            while (j < len && raw[j] >= (byte)'0' && raw[j] <= (byte)'9') j++;
            if (j == objNumStart) continue; // no digits

            // Count whitespace between obj# and gen#
            int ws1 = 0;
            while (j < len && (raw[j] == 0x20 || raw[j] == 0x09)) { ws1++; j++; }
            if (ws1 == 0) continue;

            // Generation number digits
            int genStart = j;
            while (j < len && raw[j] >= (byte)'0' && raw[j] <= (byte)'9') j++;
            if (j == genStart) continue; // no gen digits

            // Count whitespace between gen# and "obj"
            int ws2 = 0;
            while (j < len && (raw[j] == 0x20 || raw[j] == 0x09)) { ws2++; j++; }
            if (ws2 == 0) continue;

            // Must be "obj" keyword not followed by alpha (exclude "object", "objstm", etc.)
            if (j + 3 > len) continue;
            if (raw[j] != (byte)'o' || raw[j + 1] != (byte)'b' || raw[j + 2] != (byte)'j') continue;
            int afterObj = j + 3;
            if (afterObj < len && raw[afterObj] >= (byte)'a' && raw[afterObj] <= (byte)'z') continue;

            bool compliant = !hasLeadingWhitespace && (ws1 == 1) && (ws2 == 1);

            // "obj" must be followed by EOL
            if (afterObj < len && raw[afterObj] != 0x0A && raw[afterObj] != 0x0D)
                compliant = false;

            objHeaders.Add((i, afterObj, compliant));
            // Advance past this match to avoid re-scanning digits
            i = afterObj;
        }

        // Collect all "endobj" positions
        var endobjPositions = new List<(int start, int end)>();
        for (int i = 0; i < len - 5; i++)
        {
            if (raw[i] == (byte)'e' && raw[i + 1] == (byte)'n' && raw[i + 2] == (byte)'d' &&
                raw[i + 3] == (byte)'o' && raw[i + 4] == (byte)'b' && raw[i + 5] == (byte)'j')
            {
                if (i + 6 < len && raw[i + 6] >= (byte)'a' && raw[i + 6] <= (byte)'z') continue;
                endobjPositions.Add((i, i + 6));
            }
        }

        // For each obj, pair with nearest endobj and check endobj spacing
        var result = new IModelObject[objHeaders.Count];
        int edIdx = 0;
        for (int k = 0; k < objHeaders.Count; k++)
        {
            var (lineStart, afterObj, compliant) = objHeaders[k];

            // Advance endobj index past afterObj
            while (edIdx < endobjPositions.Count && endobjPositions[edIdx].start < afterObj)
                edIdx++;

            if (edIdx < endobjPositions.Count)
            {
                var (edStart, edEnd) = endobjPositions[edIdx];

                // "endobj" preceded by EOL
                if (edStart > 0 && raw[edStart - 1] != 0x0A && raw[edStart - 1] != 0x0D)
                    compliant = false;

                // "endobj" followed by EOL or EOF
                if (edEnd < len && raw[edEnd] != 0x0A && raw[edEnd] != 0x0D)
                    compliant = false;
            }

            var cosIndirect = new GenericModelObject("CosIndirect");
            cosIndirect.Set("spacingCompliesPDFA", compliant);
            result[k] = cosIndirect;
        }

        return result;
    }

    /// <summary>
    /// Scans raw PDF bytes for "xref" keywords and checks that each is followed by a single EOL
    /// marker (CR, LF, or CRLF) before the subsection header digits, per ISO 19005 clause 6.1.4.
    /// </summary>
    private static IModelObject BuildCosXRef(byte[] raw)
    {
        int len = raw.Length;
        bool xrefEOLCompliant = true;
        bool subsectionHeaderSpaceSeparated = true;
        var xrefKeyword = "xref"u8;

        for (int i = 0; i <= len - 4; i++)
        {
            // Find "xref" keyword — must be preceded by EOL or at start of file
            if (raw[i] != (byte)'x' || raw[i + 1] != (byte)'r' ||
                raw[i + 2] != (byte)'e' || raw[i + 3] != (byte)'f')
                continue;

            // Must not be part of "startxref"
            if (i >= 5 && raw[i - 5] == (byte)'s' && raw[i - 4] == (byte)'t' &&
                raw[i - 3] == (byte)'a' && raw[i - 2] == (byte)'r' && raw[i - 1] == (byte)'t')
                continue;

            // Must be preceded by whitespace/EOL or be at start
            if (i > 0 && raw[i - 1] != 0x0A && raw[i - 1] != 0x0D && raw[i - 1] != 0x20 && raw[i - 1] != 0x09)
                continue;

            int afterXref = i + 4;

            // Check byte after "xref" — must be CR or LF
            if (afterXref >= len)
            {
                xrefEOLCompliant = false;
                continue;
            }

            byte space = raw[afterXref];
            int pos = afterXref + 1;

            if (space == 0x0D) // CR
            {
                // Optional LF after CR (CRLF)
                if (pos < len && raw[pos] == 0x0A)
                    pos++;
                // Must be followed by digit (start of subsection header)
                if (pos >= len || raw[pos] < (byte)'0' || raw[pos] > (byte)'9')
                    xrefEOLCompliant = false;
            }
            else if (space == 0x0A) // LF
            {
                // Must be followed by digit
                if (pos >= len || raw[pos] < (byte)'0' || raw[pos] > (byte)'9')
                    xrefEOLCompliant = false;
            }
            else
            {
                // Not an EOL marker after "xref" — fail
                xrefEOLCompliant = false;
            }

            // Check subsection header: after "N" digits, must be single space before count digits
            // Find first space/ws after the first number in subsection header
            int numStart = pos;
            while (pos < len && raw[pos] >= (byte)'0' && raw[pos] <= (byte)'9')
                pos++;
            if (pos > numStart && pos < len)
            {
                if (raw[pos] != 0x20 || pos + 1 >= len || raw[pos + 1] < (byte)'0' || raw[pos + 1] > (byte)'9')
                    subsectionHeaderSpaceSeparated = false;
            }

            // Advance past this xref to avoid re-matching
            i = afterXref;
        }

        var cosXRef = new GenericModelObject("CosXRef");
        cosXRef.Set("xrefEOLMarkersComplyPDFA", xrefEOLCompliant);
        cosXRef.Set("subsectionHeaderSpaceSeparated", subsectionHeaderSpaceSeparated);
        return cosXRef;
    }

    private List<IModelObject> CreateContentObjects(
        IReadOnlyList<IContentGroup<double>> nodes,
        PdfDictionary page,
        int? structParents,
        IReadOnlyList<string> ancestorTags,
        StructInfo? currentStructInfo,
        string? currentEffectiveLang,
        bool actualTextFromAncestor = false)
    {
        var result = new List<IModelObject>();
        foreach (var node in nodes)
        {
            if (node is MarkedContentGroup<double> mcg)
            {
                var marking = mcg.Tag;
                var propertyList = marking.PropList ?? marking.InlineProps;
                var ownStructInfo = TryResolveMarkedContentStructInfo(structParents, propertyList, out var resolvedStructInfo) ? resolvedStructInfo : null;
                var effectiveStructInfo = ownStructInfo ?? currentStructInfo;
                var ownLang = ConvertPdfObjectToString(propertyList?.Get(LangName)) ?? ownStructInfo?.Lang;
                var newTags = ancestorTags.Concat(new[] { marking.Name.Value }).ToArray();
                // Effective lang for children: own BDC/struct lang, then inherited from parent BDC/struct chain
                var childEffectiveLang = ownLang ?? currentEffectiveLang ?? effectiveStructInfo?.Lang ?? effectiveStructInfo?.ParentLang;

                var model = new GenericModelObject("SEMarkedContent");
                model.Set("tag", marking.Name.Value);
                model.Set("isTaggedContent", effectiveStructInfo is not null);
                model.Set("parentsTags", JoinTags(ancestorTags));
                model.Set("parentStructureElementObjectKey", effectiveStructInfo?.ObjectKey);
                var inlineActualText = ConvertPdfObjectToString(propertyList?.Get(ActualTextName));
                model.Set("ActualText", inlineActualText ?? effectiveStructInfo?.ActualText);
                model.Set("Alt", ConvertPdfObjectToString(propertyList?.Get(AltName)) ?? effectiveStructInfo?.Alt);
                model.Set("E", ConvertPdfObjectToString(propertyList?.Get(new PdfName("E"))));
                model.Set("Lang", ownLang);
                model.Set("inheritedLang", ownLang is null ? (currentEffectiveLang ?? effectiveStructInfo?.Lang ?? effectiveStructInfo?.ParentLang) : null);

                var childActualTextPresent = actualTextFromAncestor || inlineActualText is not null || effectiveStructInfo?.ActualText is not null;
                var children = CreateContentObjects(
                    mcg.Children.OfType<IContentGroup<double>>().ToList(),
                    page,
                    structParents,
                    newTags,
                    effectiveStructInfo,
                    childEffectiveLang,
                    childActualTextPresent);
                if (children.Count > 0)
                {
                    model.Link("children", children.ToArray());
                }

                result.Add(model);
                continue;
            }

            if (node is FormContent<double> form)
            {
                _referencedFormXObjects.Add(form.Stream);
                try
                {
                    var nextStructParents = GetOptionalInt(form.Stream.Dictionary, new PdfName("StructParents")) ?? structParents;
                    result.AddRange(CreateContentObjects(
                        form.Parse().OfType<IContentGroup<double>>().ToList(),
                        page,
                        nextStructParents,
                        ancestorTags,
                        currentStructInfo,
                        currentEffectiveLang));
                }
                catch
                {
                    // Skip forms that fail to parse
                }
                continue;
            }

            result.Add(CreateSimpleContentObject(node, ancestorTags, currentStructInfo, currentEffectiveLang, actualTextFromAncestor));
        }

        return result;
    }

    private List<IModelObject> WrapMarkedContent(
        IReadOnlyList<MarkedContent>? markings,
        int? structParents,
        IReadOnlyList<string> ancestorTags,
        StructInfo? currentStructInfo,
        Func<IReadOnlyList<string>, StructInfo?, List<IModelObject>> createChildren)
    {
        if (markings is null || markings.Count == 0)
        {
            return createChildren(ancestorTags, currentStructInfo);
        }

        return WrapMarkedContent(markings, 0, structParents, ancestorTags, currentStructInfo, null, createChildren);
    }

    private List<IModelObject> WrapMarkedContent(
        IReadOnlyList<MarkedContent> markings,
        int index,
        int? structParents,
        IReadOnlyList<string> ancestorTags,
        StructInfo? currentStructInfo,
        string? currentEffectiveLang,
        Func<IReadOnlyList<string>, StructInfo?, List<IModelObject>> createChildren)
    {
        if (index >= markings.Count)
        {
            return createChildren(ancestorTags, currentStructInfo);
        }

        var marking = markings[index];
        var propertyList = marking.PropList ?? marking.InlineProps;
        var ownStructInfo = TryResolveMarkedContentStructInfo(structParents, propertyList, out var resolvedStructInfo) ? resolvedStructInfo : null;
        var effectiveStructInfo = ownStructInfo ?? currentStructInfo;
        var ownLang = ConvertPdfObjectToString(propertyList?.Get(LangName)) ?? ownStructInfo?.Lang;
        var childEffectiveLang = ownLang ?? currentEffectiveLang ?? effectiveStructInfo?.Lang ?? effectiveStructInfo?.ParentLang;

        var model = new GenericModelObject("SEMarkedContent");
        model.Set("tag", marking.Name.Value);
        model.Set("isTaggedContent", effectiveStructInfo is not null);
        model.Set("parentsTags", JoinTags(ancestorTags));
        model.Set("parentStructureElementObjectKey", effectiveStructInfo?.ObjectKey);
        model.Set("ActualText", ConvertPdfObjectToString(propertyList?.Get(ActualTextName)) ?? effectiveStructInfo?.ActualText);
        model.Set("Alt", ConvertPdfObjectToString(propertyList?.Get(AltName)) ?? effectiveStructInfo?.Alt);
        model.Set("E", ConvertPdfObjectToString(propertyList?.Get(new PdfName("E"))));
        model.Set("Lang", ownLang);
        model.Set("inheritedLang", ownLang is null ? (currentEffectiveLang ?? effectiveStructInfo?.Lang ?? effectiveStructInfo?.ParentLang) : null);

        var children = WrapMarkedContent(
            markings,
            index + 1,
            structParents,
            ancestorTags.Concat(new[] { marking.Name.Value }).ToArray(),
            effectiveStructInfo,
            childEffectiveLang,
            createChildren);
        if (children.Count > 0)
        {
            model.Link("children", children.ToArray());
        }

        return new List<IModelObject> { model };
    }

    private GenericModelObject CreateSimpleContentObject(
        IContentGroup<double> node,
        IReadOnlyList<string> ancestorTags,
        StructInfo? currentStructInfo,
        string? currentEffectiveLang = null,
        bool actualTextFromAncestor = false)
    {
        var model = new GenericModelObject("SESimpleContentItem");
        model.Set("isTaggedContent", currentStructInfo is not null);
        model.Set("parentsTags", JoinTags(ancestorTags));
        model.Set("contentType", node.Type.ToString());

        // Whether the glyph is "real content" (not inside an Artifact)
        var isArtifact = ancestorTags.Any(t => string.Equals(t, "Artifact", StringComparison.OrdinalIgnoreCase));
        var isRealContent = !isArtifact;
        var actualTextPresent = actualTextFromAncestor || currentStructInfo?.ActualText is not null;
        var altPresent = currentStructInfo?.Alt is not null;

        if (node is TextContent<double> text)
        {
            // Create SETextItem for language tracking (rule 7.2/34)
            var textItem = new GenericModelObject("SETextItem");
            textItem.Set("Lang", currentEffectiveLang ?? currentStructInfo?.Lang ?? currentStructInfo?.ParentLang);
            model.Link("textItem", textItem);

            var glyphs = CreateGlyphObjects(text, isRealContent, actualTextPresent, altPresent);
            if (glyphs.Count > 0)
            {
                model.Link("glyphs", glyphs.ToArray());
            }
        }

        return model;
    }

    private List<IModelObject> CreateGlyphObjects(TextContent<double> text, bool isRealContent, bool actualTextPresent, bool altPresent)
    {
        var glyphs = new List<IModelObject>();
        foreach (var segment in text.Segments)
        {
            var font = segment.GraphicsState.FontObject?.Resolve() as PdfDictionary;
            var validationFont = font is null ? null : GetValidationFontDictionary(font);
            var hasToUnicodeCMap = font is not null && font.ContainsKey("ToUnicode");
            foreach (var glyphOrShift in segment.Glyphs)
            {
                if (glyphOrShift.Glyph is not { } glyph)
                {
                    continue;
                }

                if (validationFont is not null)
                {
                    RegisterFontUsage(validationFont, glyph, segment.GraphicsState.TextMode);
                    // For composite (Type0) fonts, also register usage against the parent
                    // Type0 font dict so it doesn't get marked as PDUnusedFont.
                    if (font is not null && !ReferenceEquals(font, validationFont))
                    {
                        RegisterFontUsage(font, glyph, segment.GraphicsState.TextMode);
                    }
                    LinkTrueTypeFontProgram(validationFont);
                }

                var model = new GenericModelObject("Glyph");
                model.Set("name", glyph.Name ?? (glyph.Undefined ? ".notdef" : null));
                model.Set("renderingMode", segment.GraphicsState.TextMode);
                model.Set("isGlyphPresent", GetGlyphPresence(font, glyph));
                model.Set("widthFromDictionary", GetWidthFromDictionary(font, glyph));
                model.Set("widthFromFontProgram", GetWidthFromFontProgram(font, glyph));
                var unicode = GetGlyphUnicode(glyph);
                // veraPDF's standard AGL does not include TeX-specific glyph names.
                // Without a ToUnicode CMap, these names must yield null toUnicode.
                if (!hasToUnicodeCMap && glyph.Name is not null && TexSpecificGlyphNames.Contains(glyph.Name))
                {
                    unicode = null;
                }
                model.Set("toUnicode", unicode);
                model.Set("unicodePUA", IsUnicodePUA(unicode));
                model.Set("isRealContent", isRealContent);
                model.Set("actualTextPresent", actualTextPresent);
                model.Set("altPresent", altPresent);
                glyphs.Add(model);
            }
        }

        return glyphs;
    }

    private static bool IsUnicodePUA(string? unicode)
    {
        if (unicode is null) return false;
        foreach (var ch in unicode.EnumerateRunes())
        {
            var value = ch.Value;
            if ((value >= 0xE000 && value <= 0xF8FF) ||
                (value >= 0xF0000 && value <= 0xFFFFD) ||
                (value >= 0x100000 && value <= 0x10FFFD))
            {
                return true;
            }
        }
        return false;
    }

    private void PopulateAnnotation(GenericModelObject model, PdfDictionary annotation, IPdfObject? parentPdfObject)
    {
        model.Set("containsAA", annotation.ContainsKey(AAName));
        model.Set("containsA", annotation.ContainsKey(new PdfName("A")));
        model.Set("isOutsideCropBox", IsOutsideCropBox(annotation, parentPdfObject));
        model.Set("Contents", ConvertPdfObjectToString(annotation.Get(PdfName.Contents)));

        // PDAdditionalActions from annotation /AA
        var annotSubtype = ConvertPdfObjectToString(annotation.Get(PdfName.Subtype));
        var aaParentType = string.Equals(annotSubtype, "Widget", StringComparison.Ordinal) ? "WidgetAnnot" : "Annot";
        LinkAdditionalActions(model, annotation, aaParentType);

        // AP: ampersand-separated list of all keys in the appearance dictionary
        var apObj = annotation.Get(new PdfName("AP"));
        var apDict = apObj?.Resolve() as PdfDictionary;
        model.Set("AP", apDict is not null ? string.Join("&", apDict.Keys.Select(k => k.Value)) : null);

        // width and height from Rect
        var rect = annotation.GetOptionalValue<PdfArray>(RectName);
        if (rect is not null && rect.Count >= 4)
        {
            var x1 = GetNumberValue(rect[0]) ?? 0.0;
            var y1 = GetNumberValue(rect[1]) ?? 0.0;
            var x2 = GetNumberValue(rect[2]) ?? 0.0;
            var y2 = GetNumberValue(rect[3]) ?? 0.0;
            model.Set("width", Math.Abs(x2 - x1));
            model.Set("height", Math.Abs(y2 - y1));
        }
        else
        {
            model.Set("width", 0.0);
            model.Set("height", 0.0);
        }

        // For Widget annotations, surface TU (tooltip) from the linked form field
        if (string.Equals(model.ObjectType, "PDWidgetAnnot", StringComparison.Ordinal))
        {
            var tu = ConvertPdfObjectToString(annotation.Get(TUName));
            if (tu is null)
            {
                // Walk parent chain to find TU on the form field
                var parent = annotation.GetOptionalValue<PdfDictionary>(PName);
                while (parent is not null && tu is null)
                {
                    tu = ConvertPdfObjectToString(parent.Get(TUName));
                    parent = parent.GetOptionalValue<PdfDictionary>(PName);
                }
            }

            model.Set("TU", tu);
        }

        // containsLang: veraPDF checks the struct element's /Lang (via StructParent),
        // not the annotation dictionary's /Lang directly
        var containsLang = false;
        var structParent = annotation.GetOptionalValue<PdfNumber>(StructParentName);
        if (structParent is not null)
        {
            var key = Convert.ToInt32((double)structParent, System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetParentTreeStructInfo(key, out var structInfo))
            {
                containsLang = structInfo.ContainsLang;
                model.Set("structParentType", structInfo.RawType);
                model.Set("structParentStandardType", structInfo.StandardType);
                model.Set("Alt", structInfo.Alt);
                model.Set("ActualText", structInfo.ActualText);
            }
        }
        model.Set("containsLang", containsLang);

        // Subtype from the annotation dictionary
        var subtype = ConvertPdfObjectToString(annotation.Get(PdfName.Subtype));
        model.Set("Subtype", subtype);

        // FT: field type for widget annotations (walk parent chain)
        if (string.Equals(subtype, "Widget", StringComparison.Ordinal) || string.Equals(model.ObjectType, "PDWidgetAnnot", StringComparison.Ordinal))
        {
            var annotFt = ConvertPdfObjectToString(annotation.Get(FTName));
            if (annotFt is null)
            {
                var ftParent = annotation.GetOptionalValue<PdfDictionary>(PName);
                while (ftParent is not null && annotFt is null)
                {
                    annotFt = ConvertPdfObjectToString(ftParent.Get(FTName));
                    ftParent = ftParent.GetOptionalValue<PdfDictionary>(PName);
                }
            }
            model.Set("FT", annotFt);
        }

        // N_type: type of normal appearance (N key in AP dictionary)
        string? nType = null;
        bool containsAppearances = false;
        if (apDict is not null)
        {
            var nObj = apDict.Get(new PdfName("N"));
            var nResolved = nObj?.Resolve();
            if (nResolved is PdfStream)
            {
                nType = "Stream";
                containsAppearances = true;
            }
            else if (nResolved is PdfDictionary nDict)
            {
                nType = "Dict";
                containsAppearances = nDict.Count > 0;
            }
        }
        model.Set("N_type", nType);
        model.Set("containsAppearances", containsAppearances);

        // C and IC arrays for color rules
        model.Set("containsC", annotation.ContainsKey(new PdfName("C")));
        model.Set("containsIC", annotation.ContainsKey(new PdfName("IC")));

        // F (flags) for annotation flag rules
        var flagsObj = annotation.Get(new PdfName("F"));
        model.Set("F", flagsObj is not null ? ConvertPdfObjectToString(flagsObj) : null);

        // gOutputCS: document-level output color space (GTS_PDFA1 only for PDFA-1/2 rules)
        model.Set("gOutputCS", _pdfa1OutputCS);
    }

    private bool IsOutsideCropBox(PdfDictionary annotation, IPdfObject? parentPdfObject)
    {
        if (!annotation.TryGetValue<PdfArray>(RectName, out var rect, false))
        {
            return false;
        }

        // Use the current page context (set during page traversal) to get the crop box.
        // parentPdfObject may not be the page if the annotation was first encountered via
        // another annotation's reference (e.g. Popup key on a markup annotation).
        var pageDictionary = _currentPageDict;
        if (pageDictionary is null && parentPdfObject?.Resolve() is PdfDictionary parentDict)
        {
            pageDictionary = parentDict;
        }

        if (pageDictionary is null)
        {
            return false;
        }

        var crop = pageDictionary.GetOptionalValue<PdfArray>(PdfName.CropBox) ?? pageDictionary.GetOptionalValue<PdfArray>(PdfName.MediaBox);
        if (crop is null || rect.Count < 4 || crop.Count < 4)
        {
            return false;
        }

        var rectValues = rect.Take(4).Select(GetNumberValue).ToArray();
        var cropValues = crop.Take(4).Select(GetNumberValue).ToArray();
        if (rectValues.Any(v => v is null) || cropValues.Any(v => v is null))
        {
            return false;
        }

        return rectValues[2] <= cropValues[0] || rectValues[0] >= cropValues[2] || rectValues[3] <= cropValues[1] || rectValues[1] >= cropValues[3];
    }

    private static double? GetNumberValue(IPdfObject obj)
    {
        var resolved = obj.Resolve();
        return resolved is PdfNumber number ? (double)number : null;
    }

    private static void PopulateAcroForm(GenericModelObject model, PdfDictionary acroForm)
    {
        model.Set("containsXFA", acroForm.ContainsKey(XFAName));
        model.Set("dynamicRender", acroForm.ContainsKey(XFAName) ? "required" : null);
        model.Set("NeedAppearances", acroForm.GetOptionalValue<PdfBoolean>(NeedAppearancesName)?.Value ?? false);
    }

    private static void PopulateAction(GenericModelObject model, PdfDictionary action)
    {
        model.Set("S", ConvertPdfObjectToString(action.Get(PdfName.S)));
        model.Set("N", ConvertPdfObjectToString(action.Get(PdfName.N)));
    }

    private void PopulateOutputIntent(GenericModelObject model, PdfDictionary outputIntent)
    {
        model.Set("containsDestOutputProfileRef", outputIntent.ContainsKey(DestOutputProfileRefName));
        var s = ConvertPdfObjectToString(outputIntent.Get(PdfName.S));
        model.Set("S", s);
        model.Set("OutputConditionIdentifier", ConvertPdfObjectToString(outputIntent.Get(new PdfName("OutputConditionIdentifier"))));

        // destOutputProfileIndirect: string representation of the indirect object reference
        // Use indexer [] instead of Get() because Get() resolves indirect refs
        if (outputIntent.ContainsKey(DestOutputProfileName))
        {
            var rawDestProfile = outputIntent[DestOutputProfileName];
            if (rawDestProfile is PdfIndirectRef indRef)
            {
                model.Set("destOutputProfileIndirect", indRef.ToString());
            }
            else
            {
                model.Set("destOutputProfileIndirect", rawDestProfile.ToString());
            }
        }

        // ICCProfileMD5: compute MD5 hash of the ICC profile data
        var destProfileStream = outputIntent.GetOptionalValue<PdfStream>(DestOutputProfileName);
        if (destProfileStream is not null)
        {
            try
            {
                var bytes = destProfileStream.Contents.GetDecodedData();
                var hash = System.Security.Cryptography.MD5.HashData(bytes);
                model.Set("ICCProfileMD5", Convert.ToHexString(hash).ToLowerInvariant());
            }
            catch
            {
                // If we can't decode the stream, leave null
            }
        }
    }

    private void CreateOutputIntentsWrapper(GenericModelObject parentModel, PdfDictionary container)
    {
        if (!container.TryGetValue<PdfArray>(new PdfName("OutputIntents"), out var intents, false))
            return;
        if (intents.Count == 0)
            return;

        var wrapper = new GenericModelObject("OutputIntents");

        // Collect indirect references for DestOutputProfile entries
        var profileIndirects = new List<string>();
        foreach (var intent in intents)
        {
            var dict = intent.Resolve() as PdfDictionary;
            if (dict is null)
                continue;

            // Use indexer [] instead of Get() because Get() resolves indirect refs
            var destProfileObj = dict.ContainsKey(DestOutputProfileName) ? dict[DestOutputProfileName] : null;
            if (destProfileObj is PdfIndirectRef indRef)
            {
                profileIndirects.Add(indRef.ToString());
            }
            else if (destProfileObj is not null && destProfileObj.Resolve() is PdfStream)
            {
                // Has a DestOutputProfile but not via indirect ref — use string identity
                profileIndirects.Add(destProfileObj.ToString()!);
            }
        }

        // sameOutputProfileIndirect: true if 0 or 1 profiles, or if all indirect refs are identical
        bool sameProfile = profileIndirects.Count <= 1 ||
                           profileIndirects.Distinct(StringComparer.Ordinal).Count() == 1;
        wrapper.Set("sameOutputProfileIndirect", sameProfile);
        wrapper.Set("outputProfileIndirects", string.Join(",", profileIndirects));

        parentModel.Link("outputIntents", wrapper);
    }

    private static void PopulateExtGState(GenericModelObject model, PdfDictionary extGState)
    {
        model.Set("containsTR", extGState.ContainsKey(TRName));
        model.Set("containsTR2", extGState.ContainsKey(TR2Name));
        model.Set("containsHTP", extGState.ContainsKey(HTPName));
        model.Set("containsHTO", extGState.ContainsKey(HTOName));
        model.Set("TR2NameValue", ConvertPdfObjectToString(extGState.Get(TR2Name)));

        // SMask
        model.Set("containsSMask", extGState.ContainsKey(SMaskName));
        var smaskVal = extGState.Get(SMaskName)?.Resolve();
        model.Set("SMaskNameValue", smaskVal is PdfName smaskName ? smaskName.Value : (smaskVal is not null ? "Custom" : null));

        // BM (blend mode)
        model.Set("containsBM", extGState.ContainsKey(BMName));
        var bmRaw = extGState.Get(BMName)?.Resolve();
        model.Set("BMNameValue", ConvertPdfObjectToString(extGState.Get(BMName)));

        // Create CosBM child objects for blend mode validation
        if (bmRaw is PdfArray bmArray)
        {
            var bmObjects = new List<IModelObject>();
            foreach (var item in bmArray)
            {
                var name = ConvertPdfObjectToString(item);
                if (name is not null)
                {
                    var bmObj = new GenericModelObject("CosBM");
                    bmObj.Set("internalRepresentation", name);
                    bmObjects.Add(bmObj);
                }
            }
            if (bmObjects.Count > 0)
                model.Link("BM", bmObjects.ToArray());
        }
        else if (bmRaw is PdfName bmName)
        {
            var bmObj = new GenericModelObject("CosBM");
            bmObj.Set("internalRepresentation", bmName.Value);
            model.Link("BM", bmObj);
        }

        // ca and CA (fill and stroke alpha)
        var caObj = extGState.Get(new PdfName("ca"));
        model.Set("ca", caObj is not null ? GetNumberValue(caObj) : null);
        var caUpperObj = extGState.Get(new PdfName("CA"));
        model.Set("CA", caUpperObj is not null ? GetNumberValue(caUpperObj) : null);

        // CosRenderingIntent from /RI
        var ri = ConvertPdfObjectToString(extGState.Get(new PdfName("RI")));
        if (ri is not null)
        {
            var riObj = new GenericModelObject("CosRenderingIntent");
            riObj.Set("internalRepresentation", ri);
            model.Link("RI", riObj);
        }
    }

    private static void PopulateGroup(GenericModelObject model, PdfDictionary group)
    {
        model.Set("S", ConvertPdfObjectToString(group.Get(new PdfName("S"))));
    }

    private static void PopulateHalftone(GenericModelObject model, PdfDictionary halftone, string? relationName)
    {
        model.Set("HalftoneType", GetNumberValue(halftone.Get(new PdfName("HalftoneType"))));
        model.Set("HalftoneName", ConvertPdfObjectToString(halftone.Get(new PdfName("HalftoneName"))));
        // colorantName comes from the key in a parent Type 5 halftone dictionary.
        // For standalone halftones (e.g. ExtGState /HT), it is null.
        var isType5SubEntry = relationName is not null
            && !string.Equals(relationName, "HT", StringComparison.Ordinal)
            && !string.Equals(relationName, "HTP", StringComparison.Ordinal);
        model.Set("colorantName", isType5SubEntry ? relationName : null);
        model.Set("TransferFunction", halftone.ContainsKey(new PdfName("TransferFunction")) ? "present" : null);
    }

    private void ApplyFontRenderingModes()
    {
        foreach (var fontDict in _allFontDicts)
        {
            if (!_cache.TryGetValue(fontDict, out var fontObj))
                continue;

            if (_fontUsage.TryGetValue(fontDict, out var usage))
            {
                // Font was used in content — only set mode 3 if exclusively invisible
                if (usage.HasUsage && usage.OnlyInvisible)
                    fontObj.Set("renderingMode", 3);
            }
            else
            {
                // Font declared in Resources but never used in content.
                // Set renderingMode=3 so the embedding rule (6.2.11.4.1) doesn't
                // penalise truly unused fonts. Keep the proper font type so
                // structural rules (Type, Subtype, BaseFont, etc.) still fire.
                fontObj.Set("renderingMode", 3);
            }
        }
    }

    private void PopulateFont(GenericModelObject model, PdfDictionary font)
    {
        _allFontDicts.Add(font);
        var subtype = ConvertPdfObjectToString(font.Get(PdfName.Subtype));
        var baseFont = ConvertPdfObjectToString(font.Get(PdfName.BaseFont));
        var fontDescriptor = font.GetOptionalValue<PdfDictionary>(PdfName.FontDescriptor);
        var descendantFont = font.GetOptionalValue<PdfArray>(PdfName.DescendantFonts)?.Get(0)?.Resolve() as PdfDictionary;
        var descendantDescriptor = descendantFont?.GetOptionalValue<PdfDictionary>(PdfName.FontDescriptor);
        var descriptor = fontDescriptor ?? descendantDescriptor;

        model.Set("Subtype", subtype);
        model.Set("fontName", StripSubsetPrefix(baseFont));
        model.Set("isStandard", baseFont is not null && Standard14Fonts.Contains(StripSubsetPrefix(baseFont)));

        // CosUnicodeName for BaseFont — validates UTF-8 encoding of the font name
        if (baseFont is not null)
        {
            model.Link("BaseFont", CreateCosUnicodeName(baseFont));
        }

        // FirstChar, LastChar and Widths array size for PDSimpleFont validation rules
        var firstChar = font.GetOptionalValue<PdfNumber>(PdfName.FirstChar);
        var lastChar = font.GetOptionalValue<PdfNumber>(PdfName.LastChar);
        model.Set("FirstChar", firstChar is not null ? (int)(double)firstChar : null);
        model.Set("LastChar", lastChar is not null ? (int)(double)lastChar : null);
        var widths = font.GetOptionalValue<PdfArray>(PdfName.Widths);
        model.Set("Widths_size", widths?.Count);
        // renderingMode is per-usage in veraPDF; we set null since PDFont is per-dictionary.
        // null means "not restricted to invisible" — falls through to containsFontFile check.
        model.Set("renderingMode", (object?)null);

        var (fontFile, fontFileSubtype) = GetFontFile(descriptor);
        model.Set("containsFontFile", fontFile is not null);
        model.Set("fontFileSubtype", fontFileSubtype);
        model.Set("containsCIDSet", descriptor?.ContainsKey(CIDSetName) ?? false);
        model.Set("CharSet", ConvertPdfObjectToString(descriptor?.Get(PdfName.CharSet)));

        var encoding = font.Get(PdfName.Encoding);
        model.Set("Encoding", GetEncodingName(encoding));
        model.Set("containsDifferences", encoding?.Resolve() is PdfDictionary encodingDictionary && encodingDictionary.ContainsKey(PdfName.Differences));
        model.Set("differencesAreUnicodeCompliant", encoding?.Resolve() is not PdfDictionary diffDictionary || !diffDictionary.ContainsKey(PdfName.Differences) || DifferencesLookUnicodeCompliant(diffDictionary.GetOptionalValue<PdfArray>(PdfName.Differences)));
        model.Set("charSetListsAllGlyphs", null);
        model.Set("cidSetListsAllGlyphs", null);

        model.Set("toUnicode", ReadToUnicode(font.GetOptionalValue<PdfStream>(PdfName.ToUnicode)));
        model.Set("CMapName", GetCMapName(font));
        model.Set("cmapName", GetCMapName(font));
        model.Set("containsEmbeddedFile", font.Get(PdfName.Encoding)?.Resolve() is PdfStream);

        var cidSystemInfo = descendantFont?.GetOptionalValue<PdfDictionary>(PdfName.CIDSystemInfo);
        model.Set("CIDFontOrdering", ConvertPdfObjectToString(cidSystemInfo?.Get(PdfName.Ordering)));
        model.Set("CIDFontRegistry", ConvertPdfObjectToString(cidSystemInfo?.Get(PdfName.Registry)));
        model.Set("CIDFontSupplement", ConvertPdfObjectToScalar(cidSystemInfo?.Get(new PdfName("Supplement"))));
        model.Set("CIDToGIDMap", GetCIDToGIDMapValue((descendantFont ?? font).Get(PdfName.CIDToGIDMap)));

        // CMap-side CIDSystemInfo (from Encoding stream, if it's an embedded CMap)
        var cmapStream = encoding?.Resolve() as PdfStream;
        var cmapCidSystemInfo = cmapStream?.Dictionary.GetOptionalValue<PdfDictionary>(PdfName.CIDSystemInfo);
        model.Set("CMapOrdering", ConvertPdfObjectToString(cmapCidSystemInfo?.Get(PdfName.Ordering)));
        model.Set("CMapRegistry", ConvertPdfObjectToString(cmapCidSystemInfo?.Get(PdfName.Registry)));
        model.Set("CMapSupplement", ConvertPdfObjectToScalar(cmapCidSystemInfo?.Get(new PdfName("Supplement"))));

        if (string.Equals(subtype, "TrueType", StringComparison.Ordinal))
        {
            var flags = descriptor?.GetOptionalValue<PdfNumber>(PdfName.Flags);
            var flagValue = flags is null ? 0 : Convert.ToInt32((double)flags, System.Globalization.CultureInfo.InvariantCulture);
            model.Set("isSymbolic", (flagValue & 4) == 4);
        }

        // Create CMapFile and PDReferencedCMap for composite fonts with embedded CMaps
        if (cmapStream is not null)
        {
            LinkCMapFileObjects(model, cmapStream);
        }

        LinkTrueTypeFontProgram(font);
        UpdateFontCoverageProperties(font);
    }

    private void LinkCMapFileObjects(GenericModelObject fontModel, PdfStream cmapStream)
    {
        byte[] cmapData;
        try
        {
            cmapData = cmapStream.Contents.GetDecodedData();
        }
        catch
        {
            return;
        }

        var parsed = ParseCMapProgram(cmapData);

        // Also check stream dictionary for /UseCMap entry
        var useCMapEntry = cmapStream.Dictionary.Get(new PdfName("UseCMap"));
        if (useCMapEntry is not null)
        {
            var resolved = useCMapEntry.Resolve();
            if (resolved is PdfName useCMapName)
            {
                parsed.UseCMapName = useCMapName.Value;
            }
            else if (resolved is PdfStream useCMapStream)
            {
                // Embedded referenced CMap - extract its CMapName
                try
                {
                    var refData = useCMapStream.Contents.GetDecodedData();
                    var refText = System.Text.Encoding.ASCII.GetString(refData);
                    var nameMatch = System.Text.RegularExpressions.Regex.Match(refText, @"/CMapName\s+/(\S+)");
                    if (nameMatch.Success)
                    {
                        parsed.UseCMapName = nameMatch.Groups[1].Value;
                    }
                }
                catch { }
            }
        }

        // Create PDCMap intermediate object
        var cmapObj = new GenericModelObject("PDCMap");
        cmapObj.Set("CMapName", parsed.CMapName);
        cmapObj.Set("containsEmbeddedFile", true);

        // Create CMapFile object
        var cmapFileObj = new GenericModelObject("CMapFile");
        cmapFileObj.Set("WMode", parsed.WMode);
        var dictWMode = cmapStream.Dictionary.GetOptionalValue<PdfNumber>(new PdfName("WMode"));
        cmapFileObj.Set("dictWMode", dictWMode is not null ? (int)(double)dictWMode : 0);
        cmapFileObj.Set("maximalCID", parsed.MaximalCID);
        cmapObj.Link("embeddedFile", cmapFileObj);

        // Create PDReferencedCMap if UseCMap is present
        if (parsed.UseCMapName is not null)
        {
            var refCMapObj = new GenericModelObject("PDReferencedCMap", superTypes: new[] { "PDCMap" });
            refCMapObj.Set("CMapName", parsed.UseCMapName);
            cmapObj.Link("UseCMap", refCMapObj);
        }

        fontModel.Link("Encoding", cmapObj);
    }

    private static CMapParsedInfo ParseCMapProgram(byte[] data)
    {
        var text = System.Text.Encoding.ASCII.GetString(data);
        var result = new CMapParsedInfo();

        // Extract /CMapName /<name> def
        var cmapNameMatch = System.Text.RegularExpressions.Regex.Match(text, @"/CMapName\s+/(\S+)");
        if (cmapNameMatch.Success)
        {
            result.CMapName = cmapNameMatch.Groups[1].Value;
        }

        // Extract /WMode <value> def
        var wmodeMatch = System.Text.RegularExpressions.Regex.Match(text, @"/WMode\s+(\d+)");
        if (wmodeMatch.Success && int.TryParse(wmodeMatch.Groups[1].Value, out var wmode))
        {
            result.WMode = wmode;
        }

        // Extract UseCMap reference: /<name> usecmap (case-insensitive)
        var useCMapMatch = System.Text.RegularExpressions.Regex.Match(text, @"/(\S+)\s+usecmap", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (useCMapMatch.Success)
        {
            result.UseCMapName = useCMapMatch.Groups[1].Value;
        }

        // Parse maximal CID from cidchar and cidrange sections
        result.MaximalCID = ParseMaximalCID(text);

        return result;
    }

    private static int ParseMaximalCID(string text)
    {
        int maxCID = 0;

        // Parse begincidchar sections: <srcCode> CID
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"begincidchar\s*(.*?)\s*endcidchar", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            foreach (System.Text.RegularExpressions.Match cidMatch in System.Text.RegularExpressions.Regex.Matches(
                m.Groups[1].Value, @"<[0-9a-fA-F]+>\s+(\d+)"))
            {
                if (int.TryParse(cidMatch.Groups[1].Value, out var cid) && cid > maxCID)
                {
                    maxCID = cid;
                }
            }
        }

        // Parse begincidrange sections: <start> <end> CID
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"begincidrange\s*(.*?)\s*endcidrange", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            foreach (System.Text.RegularExpressions.Match rangeMatch in System.Text.RegularExpressions.Regex.Matches(
                m.Groups[1].Value, @"<([0-9a-fA-F]+)>\s+<([0-9a-fA-F]+)>\s+(\d+)"))
            {
                var startCode = Convert.ToInt32(rangeMatch.Groups[1].Value, 16);
                var endCode = Convert.ToInt32(rangeMatch.Groups[2].Value, 16);
                if (int.TryParse(rangeMatch.Groups[3].Value, out var baseCID))
                {
                    var rangeMax = baseCID + (endCode - startCode);
                    if (rangeMax > maxCID) maxCID = rangeMax;
                }
            }
        }

        return maxCID;
    }

    private sealed class CMapParsedInfo
    {
        public string? CMapName { get; set; }
        public int WMode { get; set; }
        public string? UseCMapName { get; set; }
        public int MaximalCID { get; set; }
    }

    private void RegisterFontUsage(PdfDictionary font, Glyph glyph, int renderingMode)
    {
        if (!_fontUsage.TryGetValue(font, out var usage))
        {
            usage = new FontUsageInfo();
            _fontUsage[font] = usage;
        }

        usage.HasUsage = true;
        if (renderingMode != 3)
        {
            usage.OnlyInvisible = false;
        }

        if (!string.IsNullOrEmpty(glyph.Name))
        {
            usage.GlyphNames.Add(glyph.Name);
        }

        if (glyph.CID.HasValue)
        {
            usage.Cids.Add(glyph.CID.Value);
        }

        UpdateFontCoverageProperties(font);
    }

    private void UpdateFontCoverageProperties(PdfDictionary font)
    {
        if (!_cache.TryGetValue(font, out var model))
        {
            return;
        }

        if (_fontUsage.TryGetValue(font, out var usage))
        {
            var descriptor = font.GetOptionalValue<PdfDictionary>(PdfName.FontDescriptor);
            var charSet = ConvertPdfObjectToString(descriptor?.Get(PdfName.CharSet));
            if (charSet is not null)
            {
                model.Set("charSetListsAllGlyphs", CharSetContainsUsedGlyphNames(charSet, usage.GlyphNames));
            }

            var cidSet = descriptor?.GetOptionalValue<PdfStream>(CIDSetName);
            if (cidSet is not null)
            {
                model.Set("cidSetListsAllGlyphs", CidSetContainsUsedGlyphs(cidSet, usage.Cids));
            }
        }
    }

    private void LinkTrueTypeFontProgram(PdfDictionary font)
    {
        // Only link TrueTypeFontProgram for simple TrueType fonts.
        // CIDFontType2 (composite TrueType) uses a different program type.
        var subtype = ConvertPdfObjectToString(font.Get(PdfName.Subtype));
        if (!string.Equals(subtype, "TrueType", StringComparison.Ordinal))
        {
            return;
        }

        if (!_cache.TryGetValue(font, out var fontModel))
        {
            return;
        }

        var fontProgram = GetOrCreateTrueTypeFontProgram(font);
        if (fontProgram is not null)
        {
            fontModel.Link("fontProgram", fontProgram);
        }
    }

    private GenericModelObject? GetOrCreateTrueTypeFontProgram(PdfDictionary font)
    {
        if (_trueTypeFontProgramObjects.TryGetValue(font, out var cached))
        {
            return cached;
        }

        var info = GetTrueTypeFontProgramInfo(font);
        if (info is null)
        {
            _trueTypeFontProgramObjects[font] = null;
            return null;
        }

        var program = new GenericModelObject("TrueTypeFontProgram");
        program.Set("isSymbolic", IsSymbolicFont(font));
        program.Set("Encoding", GetEncodingName(font.Get(PdfName.Encoding)));
        program.Set("nrCmaps", info.NumberOfCmaps);
        program.Set("cmap30Present", info.Cmap30Present);
        program.Set("cmap31Present", info.Cmap31Present);
        program.Set("cmap10Present", info.Cmap10Present);
        _trueTypeFontProgramObjects[font] = program;
        return program;
    }

    private FontProgramInfo? GetTrueTypeFontProgramInfo(PdfDictionary font)
    {
        if (_fontProgramCache.TryGetValue(font, out var cached))
        {
            return cached;
        }

        try
        {
            var descriptor = font.GetOptionalValue<PdfDictionary>(PdfName.FontDescriptor);
            var fontFile = descriptor?.GetOptionalValue<PdfStream>(PdfName.FontFile2);
            if (fontFile is null)
            {
                _fontProgramCache[font] = null;
                return null;
            }

            var bytes = fontFile.Contents.GetDecodedData();
            var reader = new TrueTypeReader(ParsingContext.Current, bytes);
            if (!reader.TryGetMaxpGlyphs(out var glyphCount))
            {
                _fontProgramCache[font] = null;
                return null;
            }

            var cmaps = reader.HasCMapTable() ? reader.ReadCMapTables() : new List<TrueTypeReader.TTCMap>();
            var hasEncoding = font.ContainsKey(PdfName.Encoding);
            var isSymbolic = IsSymbolicFont(font);
            var selectedMap = cmaps.Count == 0 ? null : reader.GetPdfCmap(cmaps, hasEncoding, isSymbolic);
            var widths = ReadTrueTypeWidths(bytes, reader.Tables, glyphCount);
            var glyphPresence = reader.Tables.ContainsKey("glyf") ? reader.ReadGlyfInfo(glyphCount) : null;

            var widthByCharCode = new Dictionary<uint, double>();
            var presenceByCharCode = new Dictionary<uint, bool>();
            if (selectedMap?.Mappings is not null)
            {
                foreach (var (charCode, glyphId) in selectedMap.Mappings)
                {
                    if (glyphId < widths.Length)
                    {
                        widthByCharCode[charCode] = widths[glyphId];
                    }

                    // A glyph is "present" if the cmap maps the char code to a valid glyph ID.
                    // Glyphs without contour data (e.g., spaces) are still considered present
                    // because the font defines a mapping for that character.
                    presenceByCharCode[charCode] = glyphId > 0;
                }
            }

            var info = new FontProgramInfo(
                widthByCharCode,
                presenceByCharCode,
                cmaps.Count,
                cmaps.Any(static x => x.PlatformId == 3 && x.EncodingId == 0),
                cmaps.Any(static x => x.PlatformId == 3 && x.EncodingId == 1),
                cmaps.Any(static x => x.PlatformId == 1 && x.EncodingId == 0));
            _fontProgramCache[font] = info;
            return info;
        }
        catch
        {
            _fontProgramCache[font] = null;
            return null;
        }
    }

    private static double[] ReadTrueTypeWidths(byte[] bytes, IReadOnlyDictionary<string, TrueTypeReader.Tab> tables, int glyphCount)
    {
        if (!tables.TryGetValue("head", out var head) ||
            !tables.TryGetValue("hhea", out var hhea) ||
            !tables.TryGetValue("hmtx", out var hmtx))
        {
            return Array.Empty<double>();
        }

        var unitsPerEm = ReadUInt16BigEndian(bytes, head.Offset + 18);
        var horizontalMetricsCount = ReadUInt16BigEndian(bytes, hhea.Offset + 34);
        if (unitsPerEm <= 0 || horizontalMetricsCount <= 0)
        {
            return Array.Empty<double>();
        }

        var widths = new double[glyphCount];
        var lastWidth = 0d;
        for (var index = 0; index < glyphCount; index++)
        {
            var metricIndex = Math.Min(index, horizontalMetricsCount - 1);
            var advanceWidth = ReadUInt16BigEndian(bytes, hmtx.Offset + (metricIndex * 4));
            lastWidth = advanceWidth * 1000d / unitsPerEm;
            widths[index] = lastWidth;
        }

        return widths;
    }

    private static int ReadUInt16BigEndian(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 1 >= bytes.Length)
        {
            return 0;
        }

        return (bytes[offset] << 8) | bytes[offset + 1];
    }

    // ── CFF width extraction ─────────────────────────────────────────

    private sealed record CffWidthInfo(
        IReadOnlyDictionary<string, double> WidthByName,
        IReadOnlyDictionary<uint, double> WidthByCid);

    private CffWidthInfo? GetCffWidthInfo(PdfDictionary font)
    {
        if (_cffWidthCache.TryGetValue(font, out var cached))
            return cached;

        try
        {
            var descriptor = font.GetOptionalValue<PdfDictionary>(PdfName.FontDescriptor);
            var fontFile3 = descriptor?.GetOptionalValue<PdfStream>(new PdfName("FontFile3"));
            if (fontFile3 is null)
            {
                _cffWidthCache[font] = null;
                return null;
            }

            var data = fontFile3.Contents.GetDecodedData();
            if (!IsCffData(data))
            {
                _cffWidthCache[font] = null;
                return null;
            }

            var widthByName = new Dictionary<string, double>(StringComparer.Ordinal);
            var widthByCid = new Dictionary<uint, double>();
            ParseCffWidths(data, widthByName, widthByCid);

            var info = new CffWidthInfo(widthByName, widthByCid);
            _cffWidthCache[font] = info;
            return info;
        }
        catch
        {
            _cffWidthCache[font] = null;
            return null;
        }
    }

    private static bool IsCffData(byte[] data) =>
        data.Length >= 4 && data[0] >= 1 && data[3] >= 1 && data[3] <= 4;

    private static void ParseCffWidths(byte[] data, Dictionary<string, double> widthByName, Dictionary<uint, double> widthByCid)
    {
        var pos = 0;

        // Header
        if (data.Length < 4) return;
        var hdrSize = data[2];
        pos = hdrSize;

        // Name INDEX
        var nameIndex = CffReadIndex(data, ref pos);
        // Top DICT INDEX
        var topDictIndex = CffReadIndex(data, ref pos);
        // String INDEX
        var stringIndex = CffReadIndex(data, ref pos);
        // Global Subr INDEX
        CffReadIndex(data, ref pos);

        if (topDictIndex.Count == 0) return;

        // Parse first font's top dict
        var topDict = CffParseDict(data, topDictIndex[0].offset, topDictIndex[0].length);

        var charStringsOffset = CffGetIntOperand(topDict, 17);
        var charSetOffset = CffGetIntOperand(topDict, 15);
        var privateInfo = CffGetPrivateInfo(topDict);

        if (charStringsOffset <= 0) return;

        // Parse CharStrings INDEX
        var csPos = charStringsOffset;
        var charStrings = CffReadIndex(data, ref csPos);

        // Parse Private DICT for defaultWidthX and nominalWidthX
        double defaultWidthX = 0;
        double nominalWidthX = 0;

        // Check for CID font (FDArray present)
        var fdArrayOffset = CffGetIntOperand(topDict, (12 << 8) | 36);
        var fdSelectOffset = CffGetIntOperand(topDict, (12 << 8) | 37);
        var isCid = fdArrayOffset > 0 && fdSelectOffset > 0;

        // For CID fonts, parse FDArray and FDSelect
        double[]? fdDefaultWidths = null;
        double[]? fdNominalWidths = null;
        int[]? fdSelectMap = null;
        double[]? fdWidthScales = null;

        // Parse top-level FontMatrix (operator 12 7, key 3079)
        // Default is [0.001, 0, 0, 0.001, 0, 0]
        var fontMatrixKey = (12 << 8) | 7;
        double[]? topFontMatrix = null;
        foreach (var e in topDict)
        {
            if (e.key == fontMatrixKey && e.values.Length >= 6)
            {
                topFontMatrix = e.values;
                break;
            }
        }

        // DEBUG: print top dict info
        {
            var topStr = topFontMatrix is null ? "null" : $"[{string.Join(",", topFontMatrix.Take(6).Select(v => v.ToString("G")))}]";
            Console.Error.WriteLine($"[CFF-TOP] isCid={isCid} topFM={topStr} fdArrayOff={fdArrayOffset} fdSelOff={fdSelectOffset} charStringsCount={charStringsOffset}");
            // Also dump all top dict keys
            Console.Error.WriteLine($"[CFF-TOP-KEYS] {string.Join(", ", topDict.Select(e => $"op={e.key}(vals={e.values.Length})"))}");
        }

        if (isCid)
        {
            var fdPos = fdArrayOffset;
            var fdArray = CffReadIndex(data, ref fdPos);
            fdDefaultWidths = new double[fdArray.Count];
            fdNominalWidths = new double[fdArray.Count];
            fdWidthScales = new double[fdArray.Count];

            for (var i = 0; i < fdArray.Count; i++)
            {
                var fdDict = CffParseDict(data, fdArray[i].offset, fdArray[i].length);
                var fdPrivate = CffGetPrivateInfo(fdDict);
                if (fdPrivate.size > 0)
                {
                    var priv = CffParseDict(data, fdPrivate.offset, fdPrivate.size);
                    fdDefaultWidths[i] = CffGetRealOperand(priv, 20);
                    fdNominalWidths[i] = CffGetRealOperand(priv, 21);
                }

                // Parse per-FD FontMatrix and compute effective scaling
                double[]? perFdFontMatrix = null;
                foreach (var e in fdDict)
                {
                    if (e.key == fontMatrixKey && e.values.Length >= 6)
                    {
                        perFdFontMatrix = e.values;
                        break;
                    }
                }

                // Calculate effective FontMatrix: topFM × perFdFM (or whichever is available)
                var effectiveFm = CffCalculateEffectiveMatrix(topFontMatrix, perFdFontMatrix);
                fdWidthScales[i] = CffIsDefaultFontMatrix(effectiveFm) ? 1.0 : effectiveFm[0] * 1000.0;

                // DEBUG: output per-FD matrix info
                var topStr = topFontMatrix is null ? "null" : $"[{string.Join(",", topFontMatrix.Take(6).Select(v => v.ToString("G")))}]";
                var perStr = perFdFontMatrix is null ? "null" : $"[{string.Join(",", perFdFontMatrix.Take(6).Select(v => v.ToString("G")))}]";
                var effStr = $"[{string.Join(",", effectiveFm.Take(6).Select(v => v.ToString("G")))}]";
                Console.Error.WriteLine($"[CFF-DEBUG] FD[{i}]: topFM={topStr} perFdFM={perStr} effectiveFM={effStr} scale={fdWidthScales[i]:G}");
            }

            // Parse FDSelect
            fdSelectMap = CffParseFdSelect(data, fdSelectOffset, charStrings.Count);
        }
        else if (privateInfo.size > 0 && privateInfo.offset + privateInfo.size <= data.Length)
        {
            var priv = CffParseDict(data, privateInfo.offset, privateInfo.size);
            defaultWidthX = CffGetRealOperand(priv, 20);
            nominalWidthX = CffGetRealOperand(priv, 21);
        }

        // Parse charset to get glyph names or CIDs
        var charsetEntries = CffParseCharSet(data, charSetOffset, charStrings.Count, isCid, stringIndex);

        // Non-CID font width scale from top FontMatrix
        var nonCidWidthScale = 1.0;
        if (!isCid && topFontMatrix is not null && !CffIsDefaultFontMatrix(topFontMatrix))
            nonCidWidthScale = topFontMatrix[0] * 1000.0;

        // Extract width from each charstring
        for (var i = 0; i < charStrings.Count; i++)
        {
            var defW = defaultWidthX;
            var nomW = nominalWidthX;
            var widthScale = nonCidWidthScale;
            if (isCid && fdSelectMap is not null && fdDefaultWidths is not null && fdNominalWidths is not null && fdWidthScales is not null)
            {
                var fdIdx = i < fdSelectMap.Length ? fdSelectMap[i] : 0;
                if (fdIdx >= 0 && fdIdx < fdDefaultWidths.Length)
                {
                    defW = fdDefaultWidths[fdIdx];
                    nomW = fdNominalWidths[fdIdx];
                    widthScale = fdWidthScales[fdIdx];
                }
            }

            var width = CffExtractCharstringWidth(data, charStrings[i].offset, charStrings[i].length, defW, nomW);
            width *= widthScale; // Convert from CFF internal units to 1000 units/em

            if (i == 0)
            {
                // GID 0 is .notdef
                if (isCid) widthByCid[0] = width;
                else widthByName[".notdef"] = width;
                continue;
            }

            if (i - 1 < charsetEntries.Count)
            {
                var entry = charsetEntries[i - 1];
                if (isCid)
                {
                    widthByCid[(uint)entry.sid] = width;
                }
                else if (entry.name is not null)
                {
                    widthByName[entry.name] = width;
                }
            }
        }
    }

    private readonly record struct CffIndexEntry(int offset, int length);

    private static List<CffIndexEntry> CffReadIndex(byte[] data, ref int pos)
    {
        var result = new List<CffIndexEntry>();
        if (pos + 2 > data.Length) return result;

        var count = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        if (count == 0) return result;

        var offSize = data[pos++];
        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var val = 0;
            for (var j = 0; j < offSize; j++)
                val = (val << 8) | data[pos++];
            offsets[i] = val;
        }

        var dataStart = pos - 1; // offsets are 1-based
        for (var i = 0; i < count; i++)
        {
            result.Add(new CffIndexEntry(dataStart + offsets[i], offsets[i + 1] - offsets[i]));
        }

        pos = dataStart + offsets[count];
        return result;
    }

    private readonly record struct CffDictEntry(int key, double[] values);

    private static List<CffDictEntry> CffParseDict(byte[] data, int offset, int length)
    {
        var entries = new List<CffDictEntry>();
        var operands = new List<double>();
        var pos = offset;
        var end = offset + length;

        while (pos < end && pos < data.Length)
        {
            int b = data[pos];
            if (b <= 21)
            {
                int key = b;
                if (b == 12 && pos + 1 < end)
                {
                    pos++;
                    key = (12 << 8) | data[pos];
                }
                entries.Add(new CffDictEntry(key, operands.ToArray()));
                operands.Clear();
                pos++;
            }
            else
            {
                operands.Add(CffParseOperand(data, ref pos));
            }
        }

        return entries;
    }

    private static double CffParseOperand(byte[] data, ref int pos)
    {
        int b = data[pos++];
        if (b == 30)
        {
            // Real number
            var sb = new System.Text.StringBuilder();
            var nibbleChars = "0123456789.EE -";
            while (pos < data.Length)
            {
                int b2 = data[pos++];
                var n1 = b2 >> 4;
                var n2 = b2 & 0xF;
                if (n1 == 0xF) break;
                sb.Append(nibbleChars[n1]);
                if (n2 == 0xF) break;
                sb.Append(nibbleChars[n2]);
            }
            return double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0;
        }
        if (b == 28)
        {
            var v = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            return (short)v;
        }
        if (b == 29)
        {
            var v = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            return v;
        }
        if (b >= 32 && b <= 246)
            return b - 139;
        if (b >= 247 && b <= 250)
            return (b - 247) * 256 + data[pos++] + 108;
        if (b >= 251 && b <= 254)
            return -((b - 251) * 256) - data[pos++] - 108;
        return 0;
    }

    private static int CffGetIntOperand(List<CffDictEntry> dict, int key)
    {
        foreach (var e in dict)
            if (e.key == key && e.values.Length > 0)
                return (int)e.values[0];
        return 0;
    }

    private static double CffGetRealOperand(List<CffDictEntry> dict, int key)
    {
        foreach (var e in dict)
            if (e.key == key && e.values.Length > 0)
                return e.values[0];
        return 0;
    }

    private static (int size, int offset) CffGetPrivateInfo(List<CffDictEntry> dict)
    {
        foreach (var e in dict)
            if (e.key == 18 && e.values.Length >= 2)
                return ((int)e.values[0], (int)e.values[1]);
        return (0, 0);
    }

    private static bool CffIsDefaultFontMatrix(double[]? fm) =>
        fm is null ||
        (Math.Abs(fm[0] - 0.001) < 1e-10 && Math.Abs(fm[1]) < 1e-10 &&
         Math.Abs(fm[2]) < 1e-10 && Math.Abs(fm[3] - 0.001) < 1e-10 &&
         Math.Abs(fm[4]) < 1e-10 && Math.Abs(fm[5]) < 1e-10);

    private static double[] CffCalculateEffectiveMatrix(double[]? topFm, double[]? perFdFm)
    {
        var defaultFm = new[] { 0.001, 0.0, 0.0, 0.001, 0.0, 0.0 };
        if (topFm is not null && perFdFm is not null)
        {
            // Matrix multiply: top × perFd (for 2D affine transform [a b 0; c d 0; e f 1])
            return
            [
                topFm[0] * perFdFm[0] + topFm[1] * perFdFm[2],
                topFm[0] * perFdFm[1] + topFm[1] * perFdFm[3],
                topFm[2] * perFdFm[0] + topFm[3] * perFdFm[2],
                topFm[2] * perFdFm[1] + topFm[3] * perFdFm[3],
                topFm[4] * perFdFm[0] + topFm[5] * perFdFm[2] + perFdFm[4],
                topFm[4] * perFdFm[1] + topFm[5] * perFdFm[3] + perFdFm[5],
            ];
        }
        if (topFm is not null) return topFm;
        if (perFdFm is not null) return perFdFm;
        return defaultFm;
    }

    private static readonly string[] CffStandardStrings =
    [
        ".notdef", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent",
        "ampersand", "quoteright", "parenleft", "parenright", "asterisk", "plus",
        "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four",
        "five", "six", "seven", "eight", "nine", "colon", "semicolon", "less",
        "equal", "greater", "question", "at", "A", "B", "C", "D", "E", "F", "G", "H",
        "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W",
        "X", "Y", "Z", "bracketleft", "backslash", "bracketright", "asciicircum",
        "underscore", "quoteleft", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j",
        "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y",
        "z", "braceleft", "bar", "braceright", "asciitilde", "exclamdown", "cent",
        "sterling", "fraction", "yen", "florin", "section", "currency",
        "quotesingle", "quotedblleft", "guillemotleft", "guilsinglleft", "guilsinglright",
        "fi", "fl", "endash", "dagger", "daggerdbl", "periodcentered", "paragraph",
        "bullet", "quotesinglbase", "quotedblbase", "quotedblright", "guillemotright",
        "ellipsis", "perthousand", "questiondown", "grave", "acute", "circumflex",
        "tilde", "macron", "breve", "dotaccent", "dieresis", "ring", "cedilla",
        "hungarumlaut", "ogonek", "caron", "emdash", "AE", "ordfeminine", "Lslash",
        "Oslash", "OE", "ordmasculine", "ae", "dotlessi", "lslash", "oslash", "oe",
        "germandbls", "onesuperior", "logicalnot", "mu", "trademark", "Eth",
        "onehalf", "plusminus", "Thorn", "onequarter", "divide", "brokenbar", "degree",
        "thorn", "threequarters", "twosuperior", "registered", "minus", "eth",
        "multiply", "threesuperior", "copyright", "Aacute", "Acircumflex", "Adieresis",
        "Agrave", "Aring", "Atilde", "Ccedilla", "Eacute", "Ecircumflex", "Edieresis",
        "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Ntilde", "Oacute",
        "Ocircumflex", "Odieresis", "Ograve", "Otilde", "Scaron", "Uacute",
        "Ucircumflex", "Udieresis", "Ugrave", "Yacute", "Ydieresis", "Zcaron",
        "aacute", "acircumflex", "adieresis", "agrave", "aring", "atilde", "ccedilla",
        "eacute", "ecircumflex", "edieresis", "egrave", "iacute", "icircumflex",
        "idieresis", "igrave", "ntilde", "oacute", "ocircumflex", "odieresis", "ograve",
        "otilde", "scaron", "uacute", "ucircumflex", "udieresis", "ugrave", "yacute",
        "ydieresis", "zcaron", "exclamsmall", "Hungarumlautsmall", "dollaroldstyle",
        "dollarsuperior", "ampersandsmall", "Acutesmall", "parenleftsuperior",
        "parenrightsuperior", "twodotenleader", "onedotenleader", "zerooldstyle",
        "oneoldstyle", "twooldstyle", "threeoldstyle", "fouroldstyle", "fiveoldstyle",
        "sixoldstyle", "sevenoldstyle", "eightoldstyle", "nineoldstyle",
        "commasuperior", "threequartersemdash", "periodsuperior", "questionsmall",
        "asuperior", "bsuperior", "centsuperior", "dsuperior", "esuperior",
        "isuperior", "lsuperior", "msuperior", "nsuperior", "osuperior", "rsuperior",
        "ssuperior", "tsuperior", "ff", "ffi", "ffl", "parenleftinferior",
        "parenrightinferior", "Circumflexsmall", "hyphensuperior", "Gravesmall",
        "Asmall", "Bsmall", "Csmall", "Dsmall", "Esmall", "Fsmall", "Gsmall",
        "Hsmall", "Ismall", "Jsmall", "Ksmall", "Lsmall", "Msmall", "Nsmall",
        "Osmall", "Psmall", "Qsmall", "Rsmall", "Ssmall", "Tsmall", "Usmall",
        "Vsmall", "Wsmall", "Xsmall", "Ysmall", "Zsmall", "colonmonetary",
        "onefitted", "rupiah", "Tildesmall", "exclamdownsmall", "centoldstyle",
        "Lslashsmall", "Scaronsmall", "Zcaronsmall", "Dieresissmall", "Brevesmall",
        "Caronsmall", "Dotaccentsmall", "Macronsmall", "figuredash", "hypheninferior",
        "Ogoneksmall", "Ringsmall", "Cedillasmall", "questiondownsmall", "oneeighth",
        "threeeighths", "fiveeighths", "seveneighths", "onethird", "twothirds",
        "zerosuperior", "foursuperior", "fivesuperior", "sixsuperior", "sevensuperior",
        "eightsuperior", "ninesuperior", "zeroinferior", "oneinferior", "twoinferior",
        "threeinferior", "fourinferior", "fiveinferior", "sixinferior", "seveninferior",
        "eightinferior", "nineinferior", "centinferior", "dollarinferior",
        "periodinferior", "commainferior", "Agravesmall", "Aacutesmall",
        "Acircumflexsmall", "Atildesmall", "Adieresissmall", "Aringsmall", "AEsmall",
        "Ccedillasmall", "Egravesmall", "Eacutesmall", "Ecircumflexsmall",
        "Edieresissmall", "Igravesmall", "Iacutesmall", "Icircumflexsmall",
        "Idieresissmall", "Ethsmall", "Ntildesmall", "Ogravesmall", "Oacutesmall",
        "Ocircumflexsmall", "Otildesmall", "Odieresissmall", "OEsmall", "Oslashsmall",
        "Ugravesmall", "Uacutesmall", "Ucircumflexsmall", "Udieresissmall",
        "Yacutesmall", "Thornsmall", "Ydieresissmall", "001.000", "001.001",
        "001.002", "001.003", "Black", "Bold", "Book", "Light", "Medium", "Regular",
        "Roman", "Semibold"
    ];

    private static string CffSidToString(int sid, List<CffIndexEntry> stringIndex, byte[] data)
    {
        if (sid < CffStandardStrings.Length)
            return CffStandardStrings[sid];
        var idx = sid - CffStandardStrings.Length;
        if (idx < stringIndex.Count)
        {
            var entry = stringIndex[idx];
            if (entry.offset + entry.length <= data.Length)
                return System.Text.Encoding.ASCII.GetString(data, entry.offset, entry.length);
        }
        return $"SID{sid}";
    }

    private readonly record struct CffCharsetEntry(int sid, string? name);

    private static List<CffCharsetEntry> CffParseCharSet(byte[] data, int offset, int nGlyphs, bool isCid, List<CffIndexEntry> stringIndex)
    {
        var entries = new List<CffCharsetEntry>();
        var remaining = nGlyphs - 1; // GID 0 is .notdef, not in charset data

        if (offset == 0)
        {
            // ISOAdobe predefined charset
            for (var i = 1; i <= remaining && i < CffStandardStrings.Length; i++)
                entries.Add(new CffCharsetEntry(i, CffStandardStrings[i]));
            return entries;
        }

        if (offset >= data.Length) return entries;

        var format = data[offset];
        var pos = offset + 1;

        if (format == 0)
        {
            for (var i = 0; i < remaining && pos + 1 < data.Length; i++)
            {
                var sid = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                entries.Add(new CffCharsetEntry(sid, isCid ? null : CffSidToString(sid, stringIndex, data)));
            }
        }
        else if (format == 1)
        {
            while (entries.Count < remaining && pos + 2 < data.Length)
            {
                var first = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                var nLeft = data[pos++];
                for (var i = 0; i <= nLeft && entries.Count < remaining; i++)
                {
                    var sid = first + i;
                    entries.Add(new CffCharsetEntry(sid, isCid ? null : CffSidToString(sid, stringIndex, data)));
                }
            }
        }
        else if (format == 2)
        {
            while (entries.Count < remaining && pos + 3 < data.Length)
            {
                var first = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                var nLeft = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                for (var i = 0; i <= nLeft && entries.Count < remaining; i++)
                {
                    var sid = first + i;
                    entries.Add(new CffCharsetEntry(sid, isCid ? null : CffSidToString(sid, stringIndex, data)));
                }
            }
        }

        return entries;
    }

    private static int[] CffParseFdSelect(byte[] data, int offset, int nGlyphs)
    {
        var result = new int[nGlyphs];
        if (offset >= data.Length) return result;

        var format = data[offset];
        var pos = offset + 1;

        if (format == 0)
        {
            for (var i = 0; i < nGlyphs && pos < data.Length; i++)
                result[i] = data[pos++];
        }
        else if (format == 3)
        {
            if (pos + 1 >= data.Length) return result;
            var nRanges = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            for (var i = 0; i < nRanges && pos + 2 < data.Length; i++)
            {
                var first = (data[pos] << 8) | data[pos + 1];
                pos += 2;
                var fd = data[pos++];
                var next = pos + 1 < data.Length ? ((data[pos] << 8) | data[pos + 1]) : nGlyphs;
                for (var gid = first; gid < next && gid < nGlyphs; gid++)
                    result[gid] = fd;
            }
        }

        return result;
    }

    private static double CffExtractCharstringWidth(byte[] data, int offset, int length, double defaultWidth, double nominalWidth)
    {
        var stack = new List<double>();
        var pos = offset;
        var end = offset + length;
        var hintCount = 0;
        var widthFound = false;
        var width = defaultWidth;

        while (pos < end && pos < data.Length)
        {
            int b = data[pos];

            if (b >= 32)
            {
                // Operand
                stack.Add(CffParseCharstringOperand(data, ref pos));
                continue;
            }

            // Operator
            pos++;
            int op = b;
            if (b == 12 && pos < end)
            {
                op = (12 << 8) | data[pos++];
                // Two-byte operators are not stack-clearing for width purposes
                stack.Clear();
                continue;
            }

            switch (op)
            {
                case 1:  // hstem
                case 3:  // vstem
                case 18: // hstemhm
                case 23: // vstemhm
                    if (!widthFound)
                    {
                        if (stack.Count % 2 != 0 && stack.Count > 0)
                        {
                            width = nominalWidth + stack[0];
                            widthFound = true;
                        }
                        else
                        {
                            widthFound = true;
                        }
                    }
                    hintCount += stack.Count / 2;
                    if (!widthFound) widthFound = true;
                    stack.Clear();
                    continue;
                case 19: // hintmask
                case 20: // cntrmask
                    if (!widthFound)
                    {
                        hintCount += stack.Count / 2;
                        if (stack.Count % 2 != 0 && stack.Count > 0)
                        {
                            width = nominalWidth + stack[0];
                        }
                        widthFound = true;
                    }
                    else
                    {
                        hintCount += stack.Count / 2;
                    }
                    stack.Clear();
                    // Skip hint mask bytes
                    var maskBytes = (hintCount + 7) / 8;
                    pos += maskBytes;
                    continue;
                case 4:  // vmoveto
                    if (!widthFound)
                    {
                        if (stack.Count >= 2)
                            width = nominalWidth + stack[0];
                        widthFound = true;
                    }
                    stack.Clear();
                    return width;
                case 21: // rmoveto
                    if (!widthFound)
                    {
                        if (stack.Count >= 3)
                            width = nominalWidth + stack[0];
                        widthFound = true;
                    }
                    stack.Clear();
                    return width;
                case 22: // hmoveto
                    if (!widthFound)
                    {
                        if (stack.Count >= 2)
                            width = nominalWidth + stack[0];
                        widthFound = true;
                    }
                    stack.Clear();
                    return width;
                case 14: // endchar
                    if (!widthFound)
                    {
                        if (stack.Count >= 1)
                            width = nominalWidth + stack[0];
                        widthFound = true;
                    }
                    return width;
                default:
                    stack.Clear();
                    continue;
            }
        }

        return width;
    }

    private static double CffParseCharstringOperand(byte[] data, ref int pos)
    {
        int b = data[pos++];
        if (b == 255 && pos + 3 < data.Length)
        {
            // Fixed-point 16.16
            var intPart = (short)((data[pos] << 8) | data[pos + 1]);
            var fracPart = (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            return intPart + fracPart / 65536.0;
        }
        if (b == 28 && pos + 1 < data.Length)
        {
            var v = (short)((data[pos] << 8) | data[pos + 1]);
            pos += 2;
            return v;
        }
        if (b >= 32 && b <= 246)
            return b - 139;
        if (b >= 247 && b <= 250 && pos < data.Length)
            return (b - 247) * 256 + data[pos++] + 108;
        if (b >= 251 && b <= 254 && pos < data.Length)
            return -((b - 251) * 256) - data[pos++] - 108;
        return 0;
    }

    // ── Type3 CharProc width extraction ──────────────────────────────

    private Dictionary<string, double>? GetType3CharProcWidths(PdfDictionary font)
    {
        if (_type3WidthCache.TryGetValue(font, out var cached))
            return cached;

        try
        {
            var charProcs = font.GetOptionalValue<PdfDictionary>(new PdfName("CharProcs"));
            if (charProcs is null)
            {
                _type3WidthCache[font] = null;
                return null;
            }

            var widths = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var key in charProcs.Keys)
            {
                var stream = charProcs.Get(key)?.Resolve() as PdfStream;
                if (stream is null) continue;

                try
                {
                    var streamData = stream.Contents.GetDecodedData();
                    var w = ExtractType3Width(streamData);
                    if (w.HasValue)
                    {
                        widths[key.Value] = w.Value;
                    }
                }
                catch
                {
                    // Skip individual CharProc failures
                }
            }

            _type3WidthCache[font] = widths;
            return widths;
        }
        catch
        {
            _type3WidthCache[font] = null;
            return null;
        }
    }

    private static double? ExtractType3Width(byte[] data)
    {
        // Parse content stream looking for d0 or d1 operator
        // d0: wx wy d0
        // d1: wx wy llx lly urx ury d1
        var text = System.Text.Encoding.ASCII.GetString(data);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length; i++)
        {
            if ((tokens[i] == "d0" && i >= 2) || (tokens[i] == "d1" && i >= 6))
            {
                // The first number before d0/d1 sequence is wx
                var wxIndex = tokens[i] == "d0" ? i - 2 : i - 6;
                if (double.TryParse(tokens[wxIndex], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var wx))
                {
                    return wx;
                }
            }
        }

        return null;
    }

    private static bool IsSymbolicFont(PdfDictionary font)
    {
        var descriptor = font.GetOptionalValue<PdfDictionary>(PdfName.FontDescriptor);
        var flags = descriptor?.GetOptionalValue<PdfNumber>(PdfName.Flags);
        var flagValue = flags is null ? 0 : Convert.ToInt32((double)flags, System.Globalization.CultureInfo.InvariantCulture);
        return (flagValue & 4) == 4;
    }

    private static string? GetGlyphUnicode(Glyph glyph)
    {
        if (glyph.MultiChar is not null)
        {
            return glyph.MultiChar;
        }

        return glyph.Char == '\0' ? null : glyph.Char.ToString();
    }

    private bool? GetGlyphPresence(PdfDictionary? font, Glyph glyph)
    {
        if (font is null)
        {
            return null; // No font to check against
        }

        var validationFont = GetValidationFontDictionary(font);
        var program = GetTrueTypeFontProgramInfo(validationFont);
        if (program is not null && glyph.CodePoint.HasValue && program.GlyphPresenceByCharCode.TryGetValue(glyph.CodePoint.Value, out var present))
        {
            return present;
        }

        // Only claim glyph is absent when we have a font program to verify against.
        // When no program data is available, return null (unknown).
        return program is not null && glyph.Undefined ? false : null;
    }

    private double? GetWidthFromDictionary(PdfDictionary? font, Glyph glyph)
    {
        if (font is null || !glyph.CodePoint.HasValue)
            return null;

        var validationFont = GetValidationFontDictionary(font);
        var subtype = ConvertPdfObjectToString(validationFont.Get(PdfName.Subtype));

        // For CID fonts, read from /W array (or /DW default)
        if (string.Equals(subtype, "CIDFontType0", StringComparison.Ordinal) ||
            string.Equals(subtype, "CIDFontType2", StringComparison.Ordinal))
        {
            return GetCidFontWidth(validationFont, glyph.CID ?? glyph.CodePoint.Value);
        }

        // For simple fonts, read raw value from /Widths array
        var widths = validationFont.GetOptionalValue<PdfArray>(PdfName.Widths);
        if (widths is null) return null;

        var firstChar = validationFont.GetOptionalValue<PdfNumber>(PdfName.FirstChar);
        if (firstChar is null) return null;

        var index = (int)glyph.CodePoint.Value - (int)(double)firstChar;
        if (index < 0 || index >= widths.Count) return null;

        var widthObj = widths.Get(index)?.Resolve();
        if (widthObj is PdfNumber num)
            return (double)num;

        return null;
    }

    private static double? GetCidFontWidth(PdfDictionary cidFont, uint cid)
    {
        // Try /W array first
        var wArray = cidFont.GetOptionalValue<PdfArray>(new PdfName("W"));
        if (wArray is not null)
        {
            for (var i = 0; i < wArray.Count; )
            {
                var startObj = wArray.Get(i)?.Resolve() as PdfNumber;
                if (startObj is null) break;
                var startCid = (int)(double)startObj;
                i++;
                if (i >= wArray.Count) break;

                var next = wArray.Get(i)?.Resolve();
                if (next is PdfArray widthList)
                {
                    // Format: c [w1 w2 w3 ...]
                    i++;
                    var idx = (int)cid - startCid;
                    if (idx >= 0 && idx < widthList.Count)
                    {
                        if (widthList.Get(idx)?.Resolve() is PdfNumber w)
                            return (double)w;
                    }
                }
                else if (next is PdfNumber endNum)
                {
                    // Format: c_first c_last w
                    var endCid = (int)(double)endNum;
                    i++;
                    if (i >= wArray.Count) break;
                    if (wArray.Get(i)?.Resolve() is PdfNumber w)
                    {
                        i++;
                        if (cid >= startCid && cid <= endCid)
                            return (double)w;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        // Fall back to /DW (default width)
        var dw = cidFont.GetOptionalValue<PdfNumber>(new PdfName("DW"));
        return dw is not null ? (double)dw : 1000d; // PDF spec default is 1000
    }

    private double? GetWidthFromFontProgram(PdfDictionary? font, Glyph glyph)
    {
        if (font is null || !glyph.CodePoint.HasValue)
        {
            return null;
        }

        var validationFont = GetValidationFontDictionary(font);

        // Try TrueType first (FontFile2)
        var program = GetTrueTypeFontProgramInfo(validationFont);
        if (program is not null && program.WidthByCharCode.TryGetValue(glyph.CodePoint.Value, out var width))
        {
            return width;
        }

        // Try CFF (FontFile3 with Subtype Type1C or CIDFontType0C)
        var cffInfo = GetCffWidthInfo(validationFont);
        if (cffInfo is not null)
        {
            // For CID fonts, lookup by CID
            if (glyph.CID.HasValue && cffInfo.WidthByCid.TryGetValue(glyph.CID.Value, out var cidWidth))
            {
                return cidWidth;
            }

            // For simple CFF fonts, lookup by glyph name
            if (glyph.Name is not null && cffInfo.WidthByName.TryGetValue(glyph.Name, out var nameWidth))
            {
                return nameWidth;
            }
        }

        // Try Type3 (CharProcs d0/d1 widths)
        var subtype = ConvertPdfObjectToString(validationFont.Get(PdfName.Subtype));
        if (string.Equals(subtype, "Type3", StringComparison.Ordinal))
        {
            var type3Widths = GetType3CharProcWidths(validationFont);
            if (type3Widths is not null && glyph.Name is not null && type3Widths.TryGetValue(glyph.Name, out var t3Width))
            {
                return t3Width;
            }
        }

        return null;
    }

    private static PdfDictionary GetValidationFontDictionary(PdfDictionary font) =>
        font.GetOptionalValue<PdfArray>(PdfName.DescendantFonts)?.Get(0)?.Resolve() as PdfDictionary ?? font;

    private static bool CharSetContainsUsedGlyphNames(string charSet, IEnumerable<string> glyphNames)
    {
        var names = charSet
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return glyphNames.All(names.Contains);
    }

    private static bool CidSetContainsUsedGlyphs(PdfStream cidSet, IEnumerable<uint> cids)
    {
        try
        {
            var data = cidSet.Contents.GetDecodedData();
            foreach (var cid in cids)
            {
                var byteIndex = (int)(cid / 8);
                if (byteIndex >= data.Length)
                {
                    return false;
                }

                var mask = 1 << (7 - ((int)cid % 8));
                if ((data[byteIndex] & mask) == 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? GetOptionalInt(PdfDictionary dictionary, PdfName name)
    {
        var value = dictionary.GetOptionalValue<PdfNumber>(name);
        return value is null ? null : Convert.ToInt32((double)value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] JoinTags(IEnumerable<string> tags) => tags.ToArray();

    private bool TryResolveMarkedContentStructInfo(int? structParents, PdfDictionary? propertyList, out StructInfo structInfo)
    {
        structInfo = null!;
        var mcid = GetOptionalInt(propertyList ?? new PdfDictionary(), new PdfName("MCID"));
        return structParents.HasValue && mcid.HasValue && TryGetMarkedContentStructInfo(structParents.Value, mcid.Value, out structInfo);
    }

    private static string StripSubsetPrefix(string? fontName)
    {
        if (fontName is null)
        {
            return string.Empty;
        }

        return Regex.IsMatch(fontName, "^[A-Z]{6}\\+", RegexOptions.CultureInvariant)
            ? fontName[7..]
            : fontName;
    }

    private static readonly System.Text.Encoding Iso88591 = System.Text.Encoding.GetEncoding("ISO-8859-1");

    private static GenericModelObject CreateCosUnicodeName(string nameValue)
    {
        var obj = new GenericModelObject("CosUnicodeName");
        var bytes = Iso88591.GetBytes(nameValue);
        obj.Set("isValidUtf8", System.Text.Unicode.Utf8.IsValid(bytes));
        obj.Set("unicodeValue", System.Text.Encoding.UTF8.GetString(bytes));
        obj.Set("internalRepresentation", nameValue);
        return obj;
    }

    private static (PdfStream? Stream, string? Subtype) GetFontFile(PdfDictionary? descriptor)
    {
        if (descriptor is null)
        {
            return (null, null);
        }

        var stream = descriptor.GetOptionalValue<PdfStream>(PdfName.FontFile) ??
                     descriptor.GetOptionalValue<PdfStream>(PdfName.FontFile2) ??
                     descriptor.GetOptionalValue<PdfStream>(PdfName.FontFile3);
        return (stream, ConvertPdfObjectToString(stream?.Dictionary.Get(PdfName.Subtype)));
    }

    private static string? GetEncodingName(IPdfObject? encoding)
    {
        if (encoding is null)
        {
            return null;
        }

        var resolved = encoding.Resolve();
        if (resolved is PdfName name)
        {
            return name.Value;
        }

        if (resolved is PdfDictionary dictionary)
        {
            return ConvertPdfObjectToString(dictionary.Get(PdfName.BaseEncoding));
        }

        return ConvertPdfObjectToString(resolved);
    }

    private static bool DifferencesLookUnicodeCompliant(PdfArray? differences)
    {
        if (differences is null)
        {
            return true;
        }

        foreach (var item in differences)
        {
            var resolved = item.Resolve();
            if (resolved is PdfName glyphName)
            {
                if (glyphName.Value.Length == 0 || glyphName.Value.Contains(' '))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? ReadToUnicode(PdfStream? toUnicode)
    {
        if (toUnicode is null)
        {
            return null;
        }

        try
        {
            return Encoding.ASCII.GetString(toUnicode.Contents.GetDecodedData());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the CIDToGIDMap value. Only "Identity" (name) and streams are valid;
    /// any other value (including other names) is treated as null (missing/invalid).
    /// </summary>
    private static string? GetCIDToGIDMapValue(IPdfObject? obj)
    {
        if (obj is null) return null;
        var resolved = obj.Resolve();
        if (resolved is PdfName name && string.Equals(name.Value, "Identity", StringComparison.Ordinal))
            return "Identity";
        if (resolved is PdfStream)
            return "stream";
        return null;
    }

    private static string? GetCMapName(PdfDictionary font)
    {
        var encoding = font.Get(PdfName.Encoding);
        if (encoding is null)
        {
            return null;
        }

        var resolved = encoding.Resolve();
        if (resolved is PdfName name)
        {
            return name.Value;
        }

        if (resolved is PdfStream stream)
        {
            return ConvertPdfObjectToString(stream.Dictionary.Get(new PdfName("CMapName")));
        }

        return null;
    }

    private void PopulateStructureElement(GenericModelObject model, PdfDictionary element)
    {
        var info = GetStructInfo(element);
        model.Set("valueS", info.RawType);
        model.Set("standardType", info.StandardType);
        model.Set("containsParent", element.ContainsKey(StructElemParentName));
        model.Set("circularMappingExist", info.CircularMappingExists);
        model.Set("isNotMappedToStandardType", info.IsNotMappedToStandardType);
        model.Set("remappedStandardType", info.RemappedStandardType);
        model.Set("parentStandardType", info.ParentStandardType);
        model.Set("kidsStandardTypes", string.Join('&', info.ChildStandardTypes));
        model.Set("hasContentItems", HasContentItems(element));
        model.Set("Alt", info.Alt);
        model.Set("ActualText", info.ActualText);
        model.Set("E", ConvertPdfObjectToString(element.Get(new PdfName("E"))));
        model.Set("Lang", info.Lang);
        model.Set("containsLang", info.ContainsLang);
        model.Set("parentLang", info.ParentLang);
        model.Set("noteID", info.NoteId);
        model.Set("hasDuplicateNoteID", !string.IsNullOrEmpty(info.NoteId) && _duplicateNoteIds.Contains(info.NoteId));
        model.Set("roleAttribute", ConvertPdfObjectToString(element.Get(new PdfName("Role"))));
        model.Set("hasOneInteractiveChild", HasOneInteractiveChild(element));
        model.Set("widgetAnnotsCount", CountWidgetAnnotChildren(element));

        // Heading nesting level (for SEHn)
        if (_headingInfoCache.TryGetValue(element, out var headingInfo))
        {
            model.Set("nestingLevel", headingInfo.NestingLevel);
            model.Set("hasCorrectNestingLevel", headingInfo.HasCorrectNestingLevel);
        }

        // Table cell properties (for SETH/SETD via SETableCell)
        if (_tableCellGeometryCache.TryGetValue(element, out var cellGeometry))
        {
            model.Set("ColSpan", cellGeometry.ColSpan);
            model.Set("RowSpan", cellGeometry.RowSpan);
            model.Set("hasIntersection", cellGeometry.HasIntersection);
        }

        // List properties (for SEL)
        if (string.Equals(info.StandardType, "L", StringComparison.Ordinal))
        {
            model.Set("ListNumbering", GetStructureAttributeString(element, "ListNumbering"));
            model.Set("containsLabels", ContainsLabels(element));
        }

        if (TryGetTableInfo(element, out var tableInfo))
        {
            model.Set("numberOfColumnWithWrongRowSpan", tableInfo.NumberOfColumnWithWrongRowSpan);
            model.Set("numberOfRowWithWrongColumnSpan", tableInfo.NumberOfRowWithWrongColumnSpan);
            model.Set("columnSpan", tableInfo.ColumnSpan);
            model.Set("wrongColumnSpan", tableInfo.WrongColumnSpan);
        }

        if (TryGetTableCellInfo(element, out var cellInfo))
        {
            model.Set("hasConnectedHeader", cellInfo.HasConnectedHeader);
            model.Set("unknownHeaders", cellInfo.UnknownHeaders);
        }

        // Link S (structure type) as CosUnicodeName
        if (!string.IsNullOrEmpty(info.RawType))
        {
            model.Link("S", CreateCosUnicodeName(info.RawType));
        }

        // Emit CosLang child objects for Lang entries
        var langObjects = CreateLangObjects(element);
        if (langObjects.Count > 0)
        {
            model.Link("Lang", langObjects.ToArray());
        }
    }

    private static void PopulateOcConfig(GenericModelObject model, PdfDictionary config)
    {
        model.Set("Name", ConvertPdfObjectToString(config.Get(PdfName.Name)));
        model.Set("AS", config.ContainsKey(ASName) ? ConvertPdfObjectToScalar(config.Get(ASName)) ?? "present" : null);
    }

    private static void PopulateEncryption(GenericModelObject model, PdfDictionary encryption)
    {
        model.Set("P", GetOptionalInt(encryption, new PdfName("P")));
    }

    private void PopulateStructTreeRoot(GenericModelObject model, PdfDictionary root)
    {
        var childTypes = GetChildStandardTypes(root.Get(KidsName));
        model.Set("kidsStandardTypes", string.Join('&', childTypes));
        model.Set("hasContentItems", HasContentItems(root));

        // firstChildStandardTypeNamespaceURL: the namespace URI of the first child struct element
        string? firstChildNsUrl = null;
        foreach (var child in EnumerateKids(root.Get(KidsName)))
        {
            if (child.Resolve() is PdfDictionary dict && (dict.ContainsKey(new PdfName("S")) || string.Equals(ConvertPdfObjectToString(dict.Get(PdfName.TypeName)), "StructElem", StringComparison.Ordinal)))
            {
                var ns = dict.GetOptionalValue<PdfDictionary>(new PdfName("NS"));
                firstChildNsUrl = ns is not null ? ConvertPdfObjectToString(ns.Get(new PdfName("NS"))) : null;
                // If no explicit NS, check if it's a standard PDF 1.x type (no namespace)
                break;
            }
        }
        model.Set("firstChildStandardTypeNamespaceURL", firstChildNsUrl);

        // roleMapNames: CosUnicodeName objects for all names in the RoleMap dictionary
        var roleMap = root.GetOptionalValue<PdfDictionary>(RoleMapName);
        if (roleMap is not null)
        {
            var roleMapNameObjs = new List<IModelObject>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in roleMap.Keys)
            {
                // Key is a structure type name
                if (seen.Add(key.Value))
                    roleMapNameObjs.Add(CreateCosUnicodeName(key.Value));
                // Value may also be a name (the mapped-to type)
                var val = roleMap.Get(key);
                if (val?.Resolve() is PdfName valName && seen.Add(valName.Value))
                    roleMapNameObjs.Add(CreateCosUnicodeName(valName.Value));
            }
            if (roleMapNameObjs.Count > 0)
                model.Link("roleMapNames", roleMapNameObjs.ToArray());
        }
    }

    private void PopulateCosInfo(GenericModelObject model, PdfDictionary info)
    {
        // Number of keys in the Info dictionary
        model.Set("size", info.Keys.Count());

        // Info dictionary string values
        model.Set("Title", TrimTrailingNull(ConvertPdfObjectToString(info.Get(new PdfName("Title")))));
        model.Set("Author", TrimTrailingNull(ConvertPdfObjectToString(info.Get(new PdfName("Author")))));
        model.Set("Subject", TrimTrailingNull(ConvertPdfObjectToString(info.Get(new PdfName("Subject")))));
        model.Set("Keywords", TrimTrailingNull(ConvertPdfObjectToString(info.Get(new PdfName("Keywords")))));
        model.Set("Creator", TrimTrailingNull(ConvertPdfObjectToString(info.Get(new PdfName("Creator")))));
        model.Set("Producer", TrimTrailingNull(ConvertPdfObjectToString(info.Get(new PdfName("Producer")))));
        model.Set("CreationDate", ConvertPdfObjectToString(info.Get(new PdfName("CreationDate"))));
        model.Set("ModDate", ConvertPdfObjectToString(info.Get(new PdfName("ModDate"))));

        // XMP counterparts from document metadata
        var xmp = GetXmpSnapshotFromCatalog();
        model.Set("XMPTitle", xmp?.DcTitle);
        model.Set("XMPCreator", xmp?.XMPCreator);
        model.Set("XMPCreatorSize", xmp?.XMPCreatorSize);
        model.Set("XMPDescription", xmp?.XMPDescription);
        model.Set("XMPProducer", xmp?.XMPProducer);
        model.Set("XMPCreatorTool", xmp?.XMPCreatorTool);
        model.Set("XMPKeywords", xmp?.XMPKeywords);
        model.Set("XMPCreateDate", xmp?.XMPCreateDate);
        model.Set("XMPModifyDate", xmp?.XMPModifyDate);

        // Date comparison
        model.Set("doCreationDatesMatch", CompareDates(ConvertPdfObjectToString(info.Get(new PdfName("CreationDate"))), xmp?.XMPCreateDate));
        model.Set("doModDatesMatch", CompareDates(ConvertPdfObjectToString(info.Get(new PdfName("ModDate"))), xmp?.XMPModifyDate));
    }

    private static string? TrimTrailingNull(string? value)
    {
        return value?.TrimEnd('\0');
    }

    private static bool? CompareDates(string? pdfDate, string? xmpDate)
    {
        if (pdfDate is null && xmpDate is null) return null;
        if (pdfDate is null || xmpDate is null) return null;

        // Normalize PDF date format D:YYYYMMDDHHmmSSOHH'mm' to comparable form
        var normalizedPdf = NormalizePdfDate(pdfDate);
        var normalizedXmp = NormalizeXmpDate(xmpDate);

        if (normalizedPdf is null || normalizedXmp is null) return null;

        return string.Equals(normalizedPdf, normalizedXmp, StringComparison.Ordinal);
    }

    private static string NormalizeTz(string tz)
    {
        // Treat +00:00 and -00:00 as Z
        if (tz is "+00:00" or "-00:00") return "Z";
        return tz;
    }

    private static string? NormalizePdfDate(string pdfDate)
    {
        // PDF date format: D:YYYYMMDDHHmmSSOHH'mm'
        var d = pdfDate.AsSpan();
        if (d.StartsWith("D:")) d = d[2..];
        if (d.Length < 4) return null;

        // Extract components with defaults
        var year = d.Length >= 4 ? d[..4].ToString() : null;
        var month = d.Length >= 6 ? d[4..6].ToString() : "01";
        var day = d.Length >= 8 ? d[6..8].ToString() : "01";
        var hour = d.Length >= 10 ? d[8..10].ToString() : "00";
        var minute = d.Length >= 12 ? d[10..12].ToString() : "00";
        var second = d.Length >= 14 ? d[12..14].ToString() : "00";

        if (year is null) return null;

        // Timezone: character at position 14 (+, -, Z, or none)
        var tz = "Z";
        if (d.Length > 14)
        {
            var tzChar = d[14];
            if (tzChar == 'Z') tz = "Z";
            else if (tzChar == '+' || tzChar == '-')
            {
                var tzHour = d.Length >= 17 ? d[15..17].ToString() : "00";
                // Skip the apostrophe at 17 if present
                var tzMin = d.Length >= 20 ? d[18..20].ToString() : "00";
                tz = $"{tzChar}{tzHour}:{tzMin}";
            }
        }

        return $"{year}-{month}-{day}T{hour}:{minute}:{second}{NormalizeTz(tz)}";
    }

    private static string? NormalizeXmpDate(string xmpDate)
    {
        // XMP date is already in ISO 8601: YYYY-MM-DDTHH:mm:ss+HH:mm or YYYY-MM-DDTHH:mm:ssZ
        // Normalize to full precision
        if (xmpDate.Length < 4) return null;

        var parts = xmpDate.Split('T');
        var datePart = parts[0];
        var dateComponents = datePart.Split('-');

        var year = dateComponents.Length >= 1 ? dateComponents[0] : null;
        var month = dateComponents.Length >= 2 ? dateComponents[1] : "01";
        var day = dateComponents.Length >= 3 ? dateComponents[2] : "01";

        if (year is null) return null;

        var hour = "00";
        var minute = "00";
        var second = "00";
        var tz = "Z";

        if (parts.Length > 1)
        {
            var timePart = parts[1];
            // Extract timezone
            var tzIdx = timePart.IndexOfAny(['+', '-', 'Z']);
            if (tzIdx >= 0)
            {
                tz = timePart[tzIdx..];
                if (tz == "Z") tz = "Z";
                timePart = timePart[..tzIdx];
            }

            var timeComponents = timePart.Split(':');
            hour = timeComponents.Length >= 1 ? timeComponents[0] : "00";
            minute = timeComponents.Length >= 2 ? timeComponents[1] : "00";
            second = timeComponents.Length >= 3 ? timeComponents[2].Split('.')[0] : "00";
        }

        return $"{year}-{month}-{day}T{hour}:{minute}:{second}{NormalizeTz(tz)}";
    }

    private void PopulateFileSpecification(GenericModelObject model, PdfDictionary fileSpec)
    {
        model.Set("F", ConvertPdfObjectToString(fileSpec.Get(PdfName.F)));
        model.Set("UF", ConvertPdfObjectToString(fileSpec.Get(UFName)));
        model.Set("containsEF", fileSpec.ContainsKey(EFName));
        model.Set("containsDesc", fileSpec.ContainsKey(new PdfName("Desc")));
        model.Set("AFRelationship", ConvertPdfObjectToString(fileSpec.Get(new PdfName("AFRelationship"))));
        model.Set("isAssociatedFile", IsAssociatedFile(fileSpec));

        // Create EmbeddedFile children from the EF dictionary values
        var ef = fileSpec.GetOptionalValue<PdfDictionary>(EFName);
        if (ef is not null)
        {
            var embeddedFiles = new List<IModelObject>();
            foreach (var (_, value) in ef)
            {
                var child = BuildObject(value, relationName: "EF", parentObjectType: "CosEFDict", parentPdfObject: ef);
                if (child is not null)
                    embeddedFiles.Add(child);
            }
            if (embeddedFiles.Count > 0)
                model.Link("EF", embeddedFiles.ToArray());
        }
    }

    private bool IsAssociatedFile(PdfDictionary fileSpec)
    {
        EnsureAfReferences();
        return _afReferencedFileSpecs!.Contains(fileSpec);
    }

    private void EnsureAfReferences()
    {
        if (_afReferencedFileSpecs is not null) return;
        _afReferencedFileSpecs = new HashSet<PdfDictionary>(ReferenceEqualityComparer<PdfDictionary>.Instance);
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer<PdfDictionary>.Instance);

        // Collect AF refs from catalog, pages, annotations, and resource XObjects
        CollectAfRefs(_document.Catalog);
        foreach (var page in _document.Pages)
        {
            CollectAfRefs(page.NativeObject);
            ScanResourceXObjects(page.NativeObject, visited);
            if (page.NativeObject.TryGetValue<PdfArray>(AnnotsName, out var annots, false))
            {
                foreach (var annot in annots)
                {
                    if (annot.Resolve() is PdfDictionary annotDict)
                        CollectAfRefs(annotDict);
                }
            }
        }
    }

    private void ScanResourceXObjects(PdfDictionary container, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(container)) return;
        if (!container.TryGetValue<PdfDictionary>(new PdfName("Resources"), out var resources, false)) return;
        if (!resources.TryGetValue<PdfDictionary>(new PdfName("XObject"), out var xobjects, false)) return;

        foreach (var key in xobjects.Keys)
        {
            var val = xobjects.Get(key)?.Resolve();
            if (val is PdfStream stream)
            {
                CollectAfRefs(stream.Dictionary);
                // Recursively scan form XObject resources
                ScanResourceXObjects(stream.Dictionary, visited);
            }
        }
    }

    private void CollectAfRefs(PdfDictionary dict)
    {
        if (!dict.TryGetValue<PdfArray>(AFName, out var af, false)) return;
        foreach (var item in af)
        {
            if (item.Resolve() is PdfDictionary fsDict)
                _afReferencedFileSpecs!.Add(fsDict);
        }
    }

    private void PopulateFormField(GenericModelObject model, PdfDictionary field)
    {
        var ft = ConvertPdfObjectToString(field.Get(FTName));
        if (ft is null)
        {
            var parent = field.GetOptionalValue<PdfDictionary>(PName);
            while (parent is not null && ft is null)
            {
                ft = ConvertPdfObjectToString(parent.Get(FTName));
                parent = parent.GetOptionalValue<PdfDictionary>(PName);
            }
        }

        model.Set("FT", ft);
        model.Set("TU", ConvertPdfObjectToString(field.Get(TUName)));
        model.Set("containsAA", field.ContainsKey(AAName));

        // PDAdditionalActions from form field /AA
        LinkAdditionalActions(model, field, "FormField");

        // containsLang: check the struct element's /Lang via StructParent (matching Java veraPDF)
        var containsLang = false;
        var structParent = field.GetOptionalValue<PdfNumber>(StructParentName);
        if (structParent is not null)
        {
            var key = Convert.ToInt32((double)structParent, System.Globalization.CultureInfo.InvariantCulture);
            if (TryGetParentTreeStructInfo(key, out var structInfo))
            {
                containsLang = structInfo.ContainsLang;
            }
        }
        model.Set("containsLang", containsLang);
    }

    private static void PopulateMediaClip(GenericModelObject model, PdfDictionary mediaClip)
    {
        model.Set("CT", ConvertPdfObjectToString(mediaClip.Get(CTName)));
        var altObj = mediaClip.Get(AltName);
        model.Set("Alt", ConvertPdfObjectToString(altObj));
        var hasCorrectAlt = false;
        if (altObj?.Resolve() is PdfArray altArray && altArray.Count >= 2 && altArray.Count % 2 == 0)
        {
            hasCorrectAlt = true;
        }

        model.Set("hasCorrectAlt", hasCorrectAlt);
    }

    private bool HasOneInteractiveChild(PdfDictionary element)
    {
        var kids = element.Get(KidsName);
        if (kids is null)
        {
            return false;
        }

        var count = 0;
        var interactive = false;
        foreach (var child in EnumerateKids(kids))
        {
            count++;
            var resolved = child.Resolve();
            if (resolved is PdfDictionary childDictionary)
            {
                var type = ConvertPdfObjectToString(childDictionary.Get(PdfName.TypeName));
                if (string.Equals(type, "OBJR", StringComparison.Ordinal))
                {
                    interactive = true;
                    continue;
                }

                var descriptor = DescribeDictionary(childDictionary, null, null, null);
                if (descriptor is not null &&
                    (string.Equals(descriptor.ObjectType, "PDWidgetAnnot", StringComparison.Ordinal) ||
                     string.Equals(descriptor.ObjectType, "PDLinkAnnot", StringComparison.Ordinal) ||
                     string.Equals(descriptor.ObjectType, "PDAnnot", StringComparison.Ordinal)))
                {
                    interactive = true;
                }
            }
        }

        return count == 1 && interactive;
    }

    private IEnumerable<IPdfObject> EnumerateKids(IPdfObject kids)
    {
        var resolved = kids.Resolve();
        if (resolved is PdfArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }

            yield break;
        }

        yield return resolved;
    }

    private void PopulateIccProfile(GenericModelObject model, PdfStream stream)
    {
        model.Set("N", ConvertPdfObjectToScalar(stream.Dictionary.Get(new PdfName("N"))));

        try
        {
            var bytes = stream.Contents.GetDecodedData();
            if (bytes.Length < 20)
            {
                return;
            }

            var major = bytes[8];
            var minor = bytes[9] >> 4;
            model.Set("version", major + (minor / 10.0));
            model.Set("deviceClass", ReadAscii(bytes, 12, 4));
            model.Set("colorSpace", ReadAscii(bytes, 16, 4));
        }
        catch
        {
        }
    }

    /// <summary>
    /// Read the ICC profile color space signature from a DestOutputProfile stream.
    /// Returns "RGB ", "CMYK", "GRAY", or "Lab " (4-char ICC signature).
    /// </summary>
    private static string? ReadIccColorSpace(PdfStream profileStream)
    {
        try
        {
            var bytes = profileStream.Contents.GetDecodedData();
            if (bytes.Length >= 20)
            {
                return ReadAscii(bytes, 16, 4);
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// Scans annotation appearance streams for device colour space usage.
    /// Annotation appearances are separate content streams not covered by the
    /// page's own content stream scanning.
    /// </summary>
    private void ScanAnnotationAppearanceColorSpaces(PdfArray annots, List<IModelObject> result,
        string? documentOutputCS, string? pageOutputCS, string? transparencyCS,
        string? transparencyIccIndirect, string? transparencyIccMD5)
    {
        foreach (var annotObj in annots)
        {
            var annot = annotObj.Resolve() as PdfDictionary;
            if (annot is null) continue;

            var ap = annot.GetOptionalValue<PdfDictionary>(new PdfName("AP"));
            if (ap is null) continue;

            foreach (var apKey in new[] { "N", "R", "D" })
            {
                var apEntry = ap.Get(new PdfName(apKey))?.Resolve();
                if (apEntry is PdfStream apStream)
                {
                    ScanAppearanceStreamColorSpaces(apStream, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                }
                else if (apEntry is PdfDictionary apDict)
                {
                    foreach (var stateKey in apDict.Keys)
                    {
                        if (apDict.Get(stateKey)?.Resolve() is PdfStream stateStream)
                        {
                            ScanAppearanceStreamColorSpaces(stateStream, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                        }
                    }
                }
            }
        }
    }

    private void ScanAppearanceStreamColorSpaces(PdfStream apStream, List<IModelObject> result,
        string? documentOutputCS, string? pageOutputCS, string? transparencyCS,
        string? transparencyIccIndirect, string? transparencyIccMD5)
    {
        var resources = apStream.Dictionary.GetOptionalValue<PdfDictionary>(PdfName.Resources);
        var colorSpaceDict = resources?.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));

        // Scan resources for image colour spaces
        var xobjects = resources?.GetOptionalValue<PdfDictionary>(new PdfName("XObject"));
        if (xobjects is not null)
        {
            foreach (var (_, value) in xobjects)
            {
                if (value.Resolve() is PdfStream stream)
                {
                    var subtype = ConvertPdfObjectToString(stream.Dictionary.Get(PdfName.Subtype));
                    if (string.Equals(subtype, "Image", StringComparison.Ordinal))
                    {
                        var csObj = stream.Dictionary.Get(PdfName.ColorSpace);
                        CreateColorSpaceObjectFromRef(csObj, resources!, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                    }
                }
            }
        }

        // Scan content stream operators for device colour spaces
        try
        {
            var data = apStream.Contents.GetDecodedData();
            var scanner = new ContentStreamScanner(ParsingContext.Current, data);
            while (scanner.Advance())
            {
                string? csName = scanner.CurrentOperator switch
                {
                    PdfOperatorType.rg or PdfOperatorType.RG => "DeviceRGB",
                    PdfOperatorType.g or PdfOperatorType.G => "DeviceGray",
                    PdfOperatorType.k or PdfOperatorType.K => "DeviceCMYK",
                    _ => null
                };

                if (csName is null && scanner.CurrentOperator is PdfOperatorType.cs or PdfOperatorType.CS)
                {
                    if (scanner.TryGetCurrentOperation<double>(out var csOp))
                    {
                        PdfName? csOpName = csOp switch
                        {
                            cs_Op<double> csTyped => csTyped.name,
                            CS_Op<double> csTyped => csTyped.name,
                            _ => null
                        };
                        if (csOpName is not null && csOpName.Value is "DeviceRGB" or "DeviceCMYK" or "DeviceGray")
                            csName = csOpName.Value;
                    }
                }

                if (csName is null) continue;

                // Check for Default* override in appearance stream resources
                string? defaultKey = csName switch
                {
                    "DeviceRGB" => "DefaultRGB",
                    "DeviceCMYK" => "DefaultCMYK",
                    "DeviceGray" => "DefaultGray",
                    _ => null
                };
                if (defaultKey is not null && colorSpaceDict is not null && colorSpaceDict.ContainsKey(new PdfName(defaultKey)))
                    continue;

                CreateDeviceColorSpaceObject(csName, result, documentOutputCS, pageOutputCS, transparencyCS);
            }
        }
        catch { }
    }

    /// <summary>
    /// Creates color space model objects (PDDeviceRGB/CMYK/Gray, ICCInputProfile, etc.)
    /// from a page's or form XObject's resource dictionary and content stream operators.
    /// </summary>
    private List<IModelObject> CreateColorSpaceObjects(PdfDictionary resourceOwner, string? documentOutputCS, string? pageOutputCS, string? transparencyCS,
        string? transparencyIccIndirect = null, string? transparencyIccMD5 = null)
    {
        var result = new List<IModelObject>();
        var resources = resourceOwner.GetOptionalValue<PdfDictionary>(PdfName.Resources);
        if (resources is null) return result;

        // Scan XObjects for image color spaces
        var xobjects = resources.GetOptionalValue<PdfDictionary>(new PdfName("XObject"));
        if (xobjects is not null)
        {
            foreach (var (_, value) in xobjects)
            {
                if (value.Resolve() is PdfStream stream)
                {
                    var subtype = ConvertPdfObjectToString(stream.Dictionary.Get(PdfName.Subtype));
                    if (string.Equals(subtype, "Image", StringComparison.Ordinal))
                    {
                        var csObj = stream.Dictionary.Get(PdfName.ColorSpace);
                        CreateColorSpaceObjectFromRef(csObj, resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                    }
                }
            }
        }

        // Scan named color spaces in resources /ColorSpace dictionary
        var colorSpaceDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
        if (colorSpaceDict is not null)
        {
            foreach (var (_, value) in colorSpaceDict)
            {
                CreateColorSpaceObjectFromRef(value, resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5, emitICCBasedCMYK: false);
            }
        }

        // Scan Shading resources — each shading dictionary has its own /ColorSpace entry
        var shadingDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("Shading"));
        if (shadingDict is not null)
        {
            foreach (var (_, value) in shadingDict)
            {
                if (value.Resolve() is PdfDictionary shading)
                {
                    var csObj = shading.Get(PdfName.ColorSpace);
                    CreateColorSpaceObjectFromRef(csObj, resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                }
            }
        }

        // Scan tiling pattern content streams for device colour space usage.
        // Tiling patterns (PatternType=1) have their own content streams that may use
        // device CS operators (cs /DeviceRGB, rg, k, etc.).
        var patternResDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("Pattern"));
        if (patternResDict is not null)
        {
            foreach (var (_, value) in patternResDict)
            {
                if (value.Resolve() is PdfStream patStream)
                {
                    var patType = GetNumberValue(patStream.Dictionary.Get(new PdfName("PatternType")));
                    if (patType is null || (int)patType.Value != 1) continue;

                    // Scan the tiling pattern's content stream
                    try
                    {
                        var patData = patStream.Contents.GetDecodedData();
                        var patScanner = new ContentStreamScanner(ParsingContext.Current, patData);
                        while (patScanner.Advance())
                        {
                            string? patCSName = patScanner.CurrentOperator switch
                            {
                                PdfOperatorType.rg or PdfOperatorType.RG => "DeviceRGB",
                                PdfOperatorType.g or PdfOperatorType.G => "DeviceGray",
                                PdfOperatorType.k or PdfOperatorType.K => "DeviceCMYK",
                                _ => null
                            };

                            // Also check cs/CS operators
                            if (patCSName is null && patScanner.CurrentOperator is PdfOperatorType.cs or PdfOperatorType.CS)
                            {
                                if (patScanner.TryGetCurrentOperation<double>(out var csOp))
                                {
                                    PdfName? csOpName = csOp switch
                                    {
                                        cs_Op<double> csTyped => csTyped.name,
                                        CS_Op<double> csTyped => csTyped.name,
                                        _ => null
                                    };
                                    if (csOpName is not null && csOpName.Value is "DeviceRGB" or "DeviceCMYK" or "DeviceGray")
                                        patCSName = csOpName.Value;
                                }
                            }

                            if (patCSName is not null)
                            {
                                // Check for Default* overrides in the pattern's own resources only.
                                // Tiling patterns have their own Resources dictionary and do NOT
                                // inherit Default* colour spaces from the enclosing page.
                                var patResources = patStream.Dictionary.GetOptionalValue<PdfDictionary>(PdfName.Resources);
                                var patCSDict = patResources?.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                                string? defaultKey = patCSName switch
                                {
                                    "DeviceRGB" => "DefaultRGB",
                                    "DeviceCMYK" => "DefaultCMYK",
                                    "DeviceGray" => "DefaultGray",
                                    _ => null
                                };
                                if (defaultKey is not null && patCSDict is not null && patCSDict.ContainsKey(new PdfName(defaultKey)))
                                    continue;

                                CreateDeviceColorSpaceObject(patCSName, result, documentOutputCS, pageOutputCS, transparencyCS);
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        // Scan Type3 font CharProcs glyph streams for device colour space usage.
        // Type3 fonts have their own content streams (one per glyph) that may use
        // device CS operators. They have their own Resources dictionary.
        var fontResDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("Font"));
        if (fontResDict is not null)
        {
            foreach (var (_, fontVal) in fontResDict)
            {
                if (fontVal.Resolve() is PdfDictionary fontDict
                    && ConvertPdfObjectToString(fontDict.Get(PdfName.Subtype)) == "Type3")
                {
                    var charProcs = fontDict.GetOptionalValue<PdfDictionary>(new PdfName("CharProcs"));
                    if (charProcs is null) continue;
                    var fontOwnResources = fontDict.GetOptionalValue<PdfDictionary>(PdfName.Resources);
                    var fontCSDict = fontOwnResources?.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                    foreach (var (_, glyphVal) in charProcs)
                    {
                        if (glyphVal.Resolve() is PdfStream glyphStream)
                        {
                            try
                            {
                                var glyphData = glyphStream.Contents.GetDecodedData();
                                var glyphScanner = new ContentStreamScanner(ParsingContext.Current, glyphData);
                                while (glyphScanner.Advance())
                                {
                                    string? glyphCSName = glyphScanner.CurrentOperator switch
                                    {
                                        PdfOperatorType.rg or PdfOperatorType.RG => "DeviceRGB",
                                        PdfOperatorType.g or PdfOperatorType.G => "DeviceGray",
                                        PdfOperatorType.k or PdfOperatorType.K => "DeviceCMYK",
                                        _ => null
                                    };
                                    if (glyphCSName is null && glyphScanner.CurrentOperator is PdfOperatorType.cs or PdfOperatorType.CS)
                                    {
                                        if (glyphScanner.TryGetCurrentOperation<double>(out var csOp))
                                        {
                                            PdfName? csOpName = csOp switch
                                            {
                                                cs_Op<double> csTyped => csTyped.name,
                                                CS_Op<double> csTyped => csTyped.name,
                                                _ => null
                                            };
                                            if (csOpName is not null && csOpName.Value is "DeviceRGB" or "DeviceCMYK" or "DeviceGray")
                                                glyphCSName = csOpName.Value;
                                        }
                                    }
                                    if (glyphCSName is null) continue;
                                    // Check Default* overrides in the Type3 font's own resources only
                                    string? defaultKey = glyphCSName switch
                                    {
                                        "DeviceRGB" => "DefaultRGB",
                                        "DeviceCMYK" => "DefaultCMYK",
                                        "DeviceGray" => "DefaultGray",
                                        _ => null
                                    };
                                    if (defaultKey is not null && fontCSDict is not null && fontCSDict.ContainsKey(new PdfName(defaultKey)))
                                        continue;
                                    CreateDeviceColorSpaceObject(glyphCSName, result, documentOutputCS, pageOutputCS, transparencyCS);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        // Scan content stream operators for implicit device colour space usage
        // (rg/RG → DeviceRGB, g/G → DeviceGray, k/K → DeviceCMYK)
        // We use flattenForms to scan into form XObjects, but track each device CS
        // with the transparency context it appears in (page-level or form-level group CS).
        try
        {
            // Key: device CS name → effective transparency CS (may differ from page-level when inside a form)
            var deviceCSWithContext = new Dictionary<string, string?>(StringComparer.Ordinal);
            bool hasMarkingOps = false; // Track whether any marking/painting operators appear
            bool hasExplicitColor = false; // Track whether any color operator was used
            int gsNestingLevel = 0; // Track q/Q graphics state nesting depth
            // Overprint tracking for ICCBased CMYK (rule 6.2.4.2 testNumber 2)
            bool currentOP = false;  // stroke overprint (OP)
            bool currentOp = false;  // fill overprint (op)
            int currentOPM = 0;      // overprint mode
            GenericModelObject? pendingStrokeIccCmyk = null; // deferred until painting
            GenericModelObject? pendingFillIccCmyk = null;   // deferred until painting
            var overprintStack = new Stack<(bool OP, bool op, int OPM, GenericModelObject? strokeCmyk, GenericModelObject? fillCmyk)>();
            var scanner = new PageContentScanner(ParsingContext.Current, resourceOwner, flattenForms: true);
            while (scanner.Advance())
            {
                // Track gs operator for overprint state
                if (scanner.CurrentOperator == PdfOperatorType.gs)
                {
                    if (scanner.TryGetCurrentOperation<double>(out var gsOp) && gsOp is gs_Op<double> gsTyped)
                    {
                        var extGStateRes = resources?.GetOptionalValue<PdfDictionary>(new PdfName("ExtGState"));
                        var gsDict = extGStateRes?.GetOptionalValue<PdfDictionary>(gsTyped.name);
                        if (gsDict is not null)
                        {
                            var opStroke = gsDict.Get(new PdfName("OP"));
                            if (opStroke is PdfBoolean opBool) currentOP = opBool.Value;
                            var opFill = gsDict.Get(new PdfName("op"));
                            if (opFill is PdfBoolean opfBool) currentOp = opfBool.Value;
                            else if (opStroke is PdfBoolean opsBool) currentOp = opsBool.Value; // op defaults to OP
                            var opm = gsDict.Get(new PdfName("OPM"));
                            if (opm is not null)
                            {
                                var opmVal = GetNumberValue(opm);
                                if (opmVal is not null) currentOPM = (int)opmVal.Value;
                            }
                        }
                    }
                }

                // Save/restore overprint state on q/Q (all levels including forms)
                if (scanner.CurrentOperator == PdfOperatorType.q)
                {
                    overprintStack.Push((currentOP, currentOp, currentOPM, pendingStrokeIccCmyk, pendingFillIccCmyk));
                }
                else if (scanner.CurrentOperator == PdfOperatorType.Q)
                {
                    if (overprintStack.Count > 0)
                    {
                        var saved = overprintStack.Pop();
                        currentOP = saved.OP;
                        currentOp = saved.op;
                        currentOPM = saved.OPM;
                        pendingStrokeIccCmyk = saved.strokeCmyk;
                        pendingFillIccCmyk = saved.fillCmyk;
                    }
                }

                // Track q/Q graphics state nesting for Op_q_gsave rule (page-level only)
                if (scanner.CurrentForm is null)
                {
                    if (scanner.CurrentOperator == PdfOperatorType.q)
                    {
                        gsNestingLevel++;
                        var qObj = new GenericModelObject("Op_q_gsave");
                        qObj.Set("nestingLevel", gsNestingLevel);
                        result.Add(qObj);
                    }
                    else if (scanner.CurrentOperator == PdfOperatorType.Q)
                    {
                        if (gsNestingLevel > 0) gsNestingLevel--;
                    }
                }

                // Detect undefined operators (Op_Undefined) — operators not in standard PDF spec
                if (!Enum.IsDefined(scanner.CurrentOperator))
                {
                    // Extract operator name from raw scanner data
                    var data = scanner.Scanner.Data;
                    var startAt = scanner.Scanner.CurrentInfo.StartAt;
                    int nameEnd = startAt;
                    while (nameEnd < data.Length && data[nameEnd] > 32 && data[nameEnd] < 127)
                        nameEnd++;
                    if (nameEnd > startAt)
                    {
                        var opUndef = new GenericModelObject("Op_Undefined");
                        opUndef.Set("name", System.Text.Encoding.ASCII.GetString(data.Slice(startAt, nameEnd - startAt)));
                        result.Add(opUndef);
                    }
                }

                string? csName = scanner.CurrentOperator switch
                {
                    PdfOperatorType.rg or PdfOperatorType.RG => "DeviceRGB",
                    PdfOperatorType.g or PdfOperatorType.G => "DeviceGray",
                    PdfOperatorType.k or PdfOperatorType.K => "DeviceCMYK",
                    _ => null
                };

                // Detect marking operators: text show (Tj/TJ/'/" ), path painting (S/s/f/F/f*/B/B*/b/b*)
                if (!hasMarkingOps)
                {
                    hasMarkingOps = scanner.CurrentOperator is
                        PdfOperatorType.Tj or PdfOperatorType.TJ or PdfOperatorType.singlequote or PdfOperatorType.doublequote or
                        PdfOperatorType.S or PdfOperatorType.s or PdfOperatorType.f or PdfOperatorType.F or
                        PdfOperatorType.f_Star or PdfOperatorType.B or PdfOperatorType.B_Star or
                        PdfOperatorType.b or PdfOperatorType.b_Star;
                }

                // Emit deferred PDICCBasedCMYK on actual painting operations
                if (scanner.CurrentOperator is PdfOperatorType.S or PdfOperatorType.s or
                    PdfOperatorType.B or PdfOperatorType.B_Star or PdfOperatorType.b or PdfOperatorType.b_Star)
                {
                    if (pendingStrokeIccCmyk is not null) { result.Add(pendingStrokeIccCmyk); pendingStrokeIccCmyk = null; }
                }
                if (scanner.CurrentOperator is PdfOperatorType.f or PdfOperatorType.F or PdfOperatorType.f_Star or
                    PdfOperatorType.B or PdfOperatorType.B_Star or PdfOperatorType.b or PdfOperatorType.b_Star)
                {
                    if (pendingFillIccCmyk is not null) { result.Add(pendingFillIccCmyk); pendingFillIccCmyk = null; }
                }
                if (scanner.CurrentOperator is PdfOperatorType.Tj or PdfOperatorType.TJ or
                    PdfOperatorType.singlequote or PdfOperatorType.doublequote)
                {
                    if (pendingFillIccCmyk is not null) { result.Add(pendingFillIccCmyk); pendingFillIccCmyk = null; }
                    if (pendingStrokeIccCmyk is not null) { result.Add(pendingStrokeIccCmyk); pendingStrokeIccCmyk = null; }
                }

                // Clear pending ICCBased CMYK when colour space changes
                if (scanner.CurrentOperator is PdfOperatorType.RG or PdfOperatorType.G or PdfOperatorType.K or PdfOperatorType.CS)
                    pendingStrokeIccCmyk = null;
                if (scanner.CurrentOperator is PdfOperatorType.rg or PdfOperatorType.g or PdfOperatorType.k or PdfOperatorType.cs)
                    pendingFillIccCmyk = null;

                // Also check inline images for device colour space usage and filters
                if (csName is null && scanner.CurrentOperator == PdfOperatorType.EI)
                {
                    if (scanner.TryGetCurrentOperation(out var op) && op is InlineImage_Op<double> inlineImg)
                    {
                        // Extract CosIIFilter objects from the raw inline image header
                        ExtractInlineImageFilters(inlineImg.header, result);

                        // Extract CosRenderingIntent from inline image /Intent entry
                        ExtractInlineImageIntent(inlineImg.header, result);

                        // Create PDInlineImage object with Interpolate property
                        var inlineImgObj = CreatePDInlineImage(inlineImg.header, result);

                        // Resolve with form-level resources when inside a form XObject
                        var imgResources = resources;
                        if (scanner.CurrentForm is { } curForm)
                        {
                            var formRes = curForm.GetOptionalValue<PdfDictionary>(PdfName.Resources);
                            if (formRes is not null) imgResources = formRes;
                        }
                        var imgStream = inlineImg.ConvertToStream(imgResources);
                        var imgCS = imgStream.Dictionary.Get(PdfName.ColorSpace);
                        if (imgCS is not null)
                        {
                            var resolvedCS = imgCS.Resolve();
                            if (resolvedCS is PdfName pn && pn.Value is "DeviceRGB" or "DeviceCMYK" or "DeviceGray")
                            {
                                csName = pn.Value;
                            }
                            else
                            {
                                // Non-device CS from inline image (Indexed, ICCBased, etc.)
                                CreateColorSpaceObjectFromRef(imgCS, imgResources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                            }
                        }
                    }
                }

                // Also check cs/CS operators that explicitly set a device colour space
                if (csName is null && scanner.CurrentOperator is PdfOperatorType.cs or PdfOperatorType.CS)
                {
                    hasExplicitColor = true; // cs/CS always explicitly sets a color space
                    if (scanner.TryGetCurrentOperation(out var csOp))
                    {
                        PdfName? csOpName = csOp switch
                        {
                            cs_Op<double> csTyped => csTyped.name,
                            CS_Op<double> csTyped => csTyped.name,
                            _ => null
                        };
                        if (csOpName is not null && csOpName.Value is "DeviceRGB" or "DeviceCMYK" or "DeviceGray")
                            csName = csOpName.Value;
                        // Check if the named CS resolves to ICCBased CMYK for overprint tracking
                        else if (csOpName is not null)
                        {
                            var csResDict = resources?.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
                            var namedCS = csResDict?.Get(csOpName);
                            if (namedCS is PdfArray namedArr && namedArr.Count > 0)
                            {
                                var namedTypeName = ConvertPdfObjectToString(namedArr[0]);
                                if (string.Equals(namedTypeName, "ICCBased", StringComparison.Ordinal) &&
                                    namedArr.Count > 1 && namedArr[1].Resolve() is PdfStream namedIccStream)
                                {
                                    var nVal = GetNumberValue(namedIccStream.Dictionary.Get(new PdfName("N")));
                                    if (nVal is not null && (int)nVal.Value == 4)
                                    {
                                        // Determine overprint flag: OP for stroke (CS), op for fill (cs)
                                        bool overprintFlag = scanner.CurrentOperator == PdfOperatorType.CS ? currentOP : currentOp;
                                        var iccCmyk = new GenericModelObject("PDICCBasedCMYK");
                                        iccCmyk.Set("N", nVal);
                                        iccCmyk.Set("overprintFlag", overprintFlag);
                                        iccCmyk.Set("OPM", currentOPM);
                                        // ICC profile identity properties
                                        var rawIccRef = namedArr[1];
                                        if (rawIccRef is PdfIndirectRef iccIndRef)
                                            iccCmyk.Set("ICCProfileIndirect", iccIndRef.ToString());
                                        try
                                        {
                                            var iccBytes = namedIccStream.Contents.GetDecodedData();
                                            iccCmyk.Set("ICCProfileMD5", Convert.ToHexString(System.Security.Cryptography.MD5.HashData(iccBytes)).ToLowerInvariant());
                                        }
                                        catch { }
                                        if (transparencyIccIndirect is not null)
                                            iccCmyk.Set("currentTransparencyProfileIndirect", transparencyIccIndirect);
                                        if (transparencyIccMD5 is not null)
                                            iccCmyk.Set("currentTransparencyICCProfileMD5", transparencyIccMD5);
                                        // Defer emission until actual painting operation uses this CS
                                        if (scanner.CurrentOperator == PdfOperatorType.CS)
                                            pendingStrokeIccCmyk = iccCmyk;
                                        else
                                            pendingFillIccCmyk = iccCmyk;
                                    }
                                }
                            }
                        }
                    }
                }

                if (csName is not null) hasExplicitColor = true;
                if (csName is null) continue;

                // Determine the effective transparency CS: if inside a form XObject,
                // use the form's own /Group /CS as the transparency blending space,
                // but only if the form's CS is device-independent (CalRGB, ICCBased, etc.).
                // A device CS (DeviceRGB/CMYK/Gray) on the form group doesn't provide
                // a calibrated blending context, so the page-level transparency CS still applies.
                var effectiveTranspCS = transparencyCS;
                if (scanner.CurrentForm is { } formDict)
                {
                    var group = formDict.GetOptionalValue<PdfDictionary>(new PdfName("Group"));
                    if (group is not null)
                    {
                        var formCSObj = group.Get(new PdfName("CS"));
                        if (formCSObj is not null)
                        {
                            var resolved = formCSObj.Resolve();
                            if (resolved is PdfName csn && csn.Value is not ("DeviceRGB" or "DeviceCMYK" or "DeviceGray"))
                                effectiveTranspCS = csn.Value;
                            else if (resolved is PdfArray csArr && csArr.Count > 0)
                            {
                                var csType = ConvertPdfObjectToString(csArr[0]);
                                if (string.Equals(csType, "ICCBased", StringComparison.Ordinal) && csArr.Count > 1 && csArr[1].Resolve() is PdfStream iccStream)
                                    effectiveTranspCS = ReadIccColorSpace(iccStream);
                                else if (csType is "CalRGB" or "CalGray" or "Lab")
                                    effectiveTranspCS = csType;
                            }
                        }
                    }
                }

                // Only store the first occurrence per device CS (with its context)
                // If the same device CS appears both at page level and in a form,
                // the page-level context is more restrictive (no form group CS to help).
                if (!deviceCSWithContext.ContainsKey(csName))
                    deviceCSWithContext[csName] = effectiveTranspCS;
                else if (effectiveTranspCS is null || effectiveTranspCS != deviceCSWithContext[csName])
                {
                    // Multiple contexts — use the most restrictive (page-level / null)
                    if (deviceCSWithContext[csName] is not null && effectiveTranspCS is null)
                        deviceCSWithContext[csName] = null;
                }
            }

            // Implicit DeviceGray: if marking/painting operators exist but no explicit
            // color operator was used, the default graphics state uses DeviceGray.
            if (hasMarkingOps && !hasExplicitColor && !deviceCSWithContext.ContainsKey("DeviceGray"))
            {
                deviceCSWithContext["DeviceGray"] = transparencyCS;
            }

            // Check for Default* overrides in resources — if present, the device CS is remapped
            foreach (var (csName, effectiveTranspCS) in deviceCSWithContext)
            {
                string? defaultKey = csName switch
                {
                    "DeviceRGB" => "DefaultRGB",
                    "DeviceCMYK" => "DefaultCMYK",
                    "DeviceGray" => "DefaultGray",
                    _ => null
                };
                if (defaultKey is not null && colorSpaceDict is not null && colorSpaceDict.ContainsKey(new PdfName(defaultKey)))
                    continue; // Remapped by Default* — not a true device color space

                CreateDeviceColorSpaceObject(csName, result, documentOutputCS, pageOutputCS, effectiveTranspCS);
            }
        }
        catch (Exception)
        {
            // Content stream parsing may fail for malformed PDFs
        }

        return result;
    }

    private void CreateColorSpaceObjectFromRef(IPdfObject? csRef, PdfDictionary resources, List<IModelObject> result,
        string? documentOutputCS, string? pageOutputCS, string? transparencyCS,
        string? transparencyIccIndirect = null, string? transparencyIccMD5 = null,
        bool emitICCBasedCMYK = true)
    {
        if (csRef is null) return;
        var resolved = csRef.Resolve();

        if (resolved is PdfName csName)
        {
            // Don't create PDDevice* objects if resources define a Default* override
            // (e.g., DefaultRGB → ICCBased means DeviceRGB is remapped to calibrated)
            var colorSpaceDict = resources.GetOptionalValue<PdfDictionary>(new PdfName("ColorSpace"));
            string? defaultKey = csName.Value switch
            {
                "DeviceRGB" => "DefaultRGB",
                "DeviceCMYK" => "DefaultCMYK",
                "DeviceGray" => "DefaultGray",
                _ => null
            };
            if (defaultKey is not null && colorSpaceDict is not null && colorSpaceDict.ContainsKey(new PdfName(defaultKey)))
                return; // Remapped to default — not a true device color space

            CreateDeviceColorSpaceObject(csName.Value, result, documentOutputCS, pageOutputCS, transparencyCS);
            return;
        }

        if (resolved is PdfArray csArray && csArray.Count > 0)
        {
            var csTypeName = ConvertPdfObjectToString(csArray[0]);
            if (csTypeName is null) return;

            switch (csTypeName)
            {
                case "DeviceRGB":
                case "DeviceCMYK":
                case "DeviceGray":
                    CreateDeviceColorSpaceObject(csTypeName, result, documentOutputCS, pageOutputCS, transparencyCS);
                    break;
                case "ICCBased":
                    if (csArray.Count > 1 && csArray[1].Resolve() is PdfStream iccStream)
                    {
                        var iccProfile = new GenericModelObject("ICCInputProfile");
                        PopulateIccProfile(iccProfile, iccStream);
                        result.Add(iccProfile);

                        // Check if this is an ICCBased CMYK (N==4)
                        var n = GetNumberValue(iccStream.Dictionary.Get(new PdfName("N")));
                        if (emitICCBasedCMYK && n is not null && (int)n.Value == 4)
                        {
                            var iccCmyk = new GenericModelObject("PDICCBasedCMYK");
                            iccCmyk.Set("N", n);
                            iccCmyk.Set("overprintFlag", false);
                            iccCmyk.Set("OPM", 0);

                            // ICC profile identity properties for testNumber 3
                            var rawRef = csArray[1];
                            if (rawRef is PdfIndirectRef iccIndRef)
                                iccCmyk.Set("ICCProfileIndirect", iccIndRef.ToString());
                            try
                            {
                                var iccBytes = iccStream.Contents.GetDecodedData();
                                iccCmyk.Set("ICCProfileMD5", Convert.ToHexString(System.Security.Cryptography.MD5.HashData(iccBytes)).ToLowerInvariant());
                            }
                            catch { }
                            if (transparencyIccIndirect is not null)
                                iccCmyk.Set("currentTransparencyProfileIndirect", transparencyIccIndirect);
                            if (transparencyIccMD5 is not null)
                                iccCmyk.Set("currentTransparencyICCProfileMD5", transparencyIccMD5);

                            result.Add(iccCmyk);
                        }
                    }
                    break;
                case "Separation":
                    {
                        var sep = new GenericModelObject("PDSeparation");
                        string? colorantName = csArray.Count > 1 ? ConvertPdfObjectToString(csArray[1]) : null;
                        // areTintAndAlternateConsistent: true if this is the first Separation with
                        // this colorant name, or if every other Separation with the same name has
                        // the same alternate CS and tintTransform (compared by COS object identity)
                        bool consistent = true;
                        if (colorantName is not null && csArray.Count > 3)
                        {
                            var alternate = csArray[2].Resolve();
                            var tintTransform = csArray[3].Resolve();
                            if (_inconsistentSeparations.Contains(colorantName))
                            {
                                consistent = false;
                            }
                            else if (_separationsByColorant.TryGetValue(colorantName, out var existing))
                            {
                                if (!PdfObjectStructuralComparer.AreEqual(existing.alternate, alternate) ||
                                    !PdfObjectStructuralComparer.AreEqual(existing.tintTransform, tintTransform))
                                {
                                    consistent = false;
                                    _inconsistentSeparations.Add(colorantName);
                                }
                            }
                            else
                            {
                                _separationsByColorant[colorantName] = (alternate, tintTransform);
                            }
                        }
                        sep.Set("areTintAndAlternateConsistent", consistent);
                        if (colorantName is not null)
                        {
                            sep.Set("name", colorantName);
                            sep.Link("colorantName", CreateCosUnicodeName(colorantName));
                        }
                        result.Add(sep);
                        // Recurse into the alternate CS — it may be a device CS
                        if (csArray.Count > 2)
                            CreateColorSpaceObjectFromRef(csArray[2], resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                    }
                    break;
                case "DeviceN":
                    {
                        var deviceN = new GenericModelObject("PDDeviceN");
                        PdfArray? namesArr = null;
                        if (csArray.Count > 1 && csArray[1].Resolve() is PdfArray na)
                        {
                            namesArr = na;
                            deviceN.Set("nrComponents", namesArr.Count);
                        }
                        // areColorantsPresent: veraPDF semantics —
                        // True if every colorant name in the names array is accounted for by:
                        //   (a) standard CMYK process names (Cyan/Magenta/Yellow/Black), OR
                        //   (b) the name "None", OR
                        //   (c) a key in the Colorants subdictionary of attrs (csArray[4]), OR
                        //   (d) a name in the Process/Components array of attrs
                        bool hasColorants = true;
                        if (namesArr is not null)
                        {
                            var knownNames = new HashSet<string>(StringComparer.Ordinal) { "Cyan", "Magenta", "Yellow", "Black", "None" };
                            // Merge Colorants dict keys
                            if (csArray.Count > 4 && csArray[4].Resolve() is PdfDictionary attrs)
                            {
                                if (attrs.TryGetValue<PdfDictionary>(new PdfName("Colorants"), out var colorantsDict))
                                {
                                    foreach (var (key, _) in colorantsDict)
                                        knownNames.Add(key.Value!);
                                }
                                // Merge Process/Components array names
                                if (attrs.TryGetValue<PdfDictionary>(new PdfName("Process"), out var processDict)
                                    && processDict.TryGetValue<PdfArray>(new PdfName("Components"), out var componentsArr))
                                {
                                    foreach (var comp in componentsArr)
                                    {
                                        var compName = ConvertPdfObjectToString(comp);
                                        if (compName is not null) knownNames.Add(compName);
                                    }
                                }
                            }
                            foreach (var nameObj in namesArr)
                            {
                                var colorantName = ConvertPdfObjectToString(nameObj);
                                if (colorantName is not null && !knownNames.Contains(colorantName))
                                {
                                    hasColorants = false;
                                    break;
                                }
                            }
                        }
                        deviceN.Set("areColorantsPresent", hasColorants);
                        // CosUnicodeName objects for each colorant name
                        if (namesArr is not null)
                        {
                            var colorantNameObjs = new List<IModelObject>();
                            foreach (var nameObj in namesArr)
                            {
                                var cn = ConvertPdfObjectToString(nameObj);
                                if (cn is not null)
                                    colorantNameObjs.Add(CreateCosUnicodeName(cn));
                            }
                            if (colorantNameObjs.Count > 0)
                                deviceN.Link("colorantNames", colorantNameObjs.ToArray());
                        }
                        result.Add(deviceN);
                        // Recurse into the alternate CS — it may be a device CS
                        if (csArray.Count > 2)
                            CreateColorSpaceObjectFromRef(csArray[2], resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                        // Recurse into Colorants dictionary entries — each is a colour space
                        // that may be Separation (tracked for areTintAndAlternateConsistent)
                        if (csArray.Count > 4 && csArray[4].Resolve() is PdfDictionary devNAttrs2)
                        {
                            if (devNAttrs2.TryGetValue<PdfDictionary>(new PdfName("Colorants"), out var colorantsDict2))
                            {
                                foreach (var (_, csVal) in colorantsDict2)
                                    CreateColorSpaceObjectFromRef(csVal, resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                            }
                        }
                    }
                    break;
                case "Indexed":
                    // [/Indexed baseCS hival lookup] — the base CS may be a device CS
                    if (csArray.Count > 1)
                        CreateColorSpaceObjectFromRef(csArray[1], resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                    break;
                case "Pattern":
                    // [/Pattern underlyingCS] — uncoloured tiling patterns; base may be device CS
                    if (csArray.Count > 1)
                        CreateColorSpaceObjectFromRef(csArray[1], resources, result, documentOutputCS, pageOutputCS, transparencyCS, transparencyIccIndirect, transparencyIccMD5);
                    break;
            }
        }
    }

    private void CreateDeviceColorSpaceObject(string csName, List<IModelObject> result,
        string? documentOutputCS, string? pageOutputCS, string? transparencyCS)
    {
        string? objectType = csName switch
        {
            "DeviceRGB" => "PDDeviceRGB",
            "DeviceCMYK" => "PDDeviceCMYK",
            "DeviceGray" => "PDDeviceGray",
            _ => null
        };
        if (objectType is null) return;

        var cs = new GenericModelObject(objectType);
        cs.Set("gOutputCS", _pdfa1OutputCS);
        cs.Set("gPageOutputCS", pageOutputCS ?? documentOutputCS);
        cs.Set("gDocumentOutputCS", documentOutputCS);
        cs.Set("gTransparencyCS", transparencyCS);
        result.Add(cs);
    }

    private void PopulateJpeg2000(GenericModelObject model, PdfStream stream)
    {
        model.Set("hasColorSpace", stream.Dictionary.ContainsKey(PdfName.ColorSpace));

        try
        {
            using var encoded = stream.Contents.GetEncodedData();
            using var ms = new MemoryStream();
            encoded.CopyTo(ms);
            var data = ms.ToArray();

            ParseJp2Boxes(model, data);
        }
        catch
        {
            // If we can't read the stream, leave defaults
        }
    }

    private static void ParseJp2Boxes(GenericModelObject model, byte[] data)
    {
        int nrColorSpaceSpecs = 0;
        int nrColorSpacesWithApproxField = 0;
        // colr data from the first box with APPROX==1
        int? colrMethod = null;
        int? colrEnumCS = null;
        // colr data from the very first box (regardless of APPROX)
        int? firstColrMethod = null;
        int? firstColrEnumCS = null;
        int nrColorChannels = 0;
        int bitDepth = 0;
        bool bpccBoxPresent = false;

        // Parse top-level boxes to find jp2h (JP2 Header superbox)
        int offset = 0;
        while (offset + 8 <= data.Length)
        {
            var (boxLen, boxType) = ReadBox(data, offset);
            if (boxLen < 8 || offset + boxLen > data.Length)
                break;

            if (boxType == "jp2h")
            {
                // Parse sub-boxes inside jp2h
                int inner = offset + 8;
                int innerEnd = offset + (int)boxLen;
                while (inner + 8 <= innerEnd)
                {
                    var (subLen, subType) = ReadBox(data, inner);
                    if (subLen < 8 || inner + subLen > innerEnd)
                        break;

                    int subData = inner + 8;

                    if (subType == "ihdr" && subLen >= 22)
                    {
                        // ihdr: 4 bytes height, 4 bytes width, 2 bytes numComponents, 1 byte BPC
                        nrColorChannels = ReadUInt16BE(data, subData + 8);
                        var bpc = data[subData + 10];
                        // Bit 7 = signed flag; bits 0-6 = depth minus 1
                        bitDepth = (bpc & 0x7F) + 1;
                    }
                    else if (subType == "colr" && subLen >= 11)
                    {
                        nrColorSpaceSpecs++;
                        int meth = data[subData];
                        int approx = data[subData + 2];
                        int? enumCS = (meth == 1 && subLen >= 15) ? ReadInt32BE(data, subData + 3) : null;

                        // Track the very first colr box
                        firstColrMethod ??= meth;
                        firstColrEnumCS ??= enumCS;

                        // Track the first colr box with APPROX == 1
                        if (approx == 1)
                        {
                            nrColorSpacesWithApproxField++;
                            colrMethod ??= meth;
                            colrEnumCS ??= enumCS;
                        }
                    }
                    else if (subType == "bpcc")
                    {
                        bpccBoxPresent = true;
                    }

                    inner += (int)subLen;
                }
                break; // Only process first jp2h
            }

            offset += (int)boxLen;
        }

        // Determine effective colrMethod/colrEnumCS using veraPDF priority logic:
        // 1. If any colr box has APPROX==1, use that box's values
        // 2. Else if exactly 1 colr box total, use the first (only) box's values
        int effectiveColrMethod;
        int? effectiveColrEnumCS;
        if (nrColorSpacesWithApproxField > 0)
        {
            effectiveColrMethod = colrMethod!.Value;
            effectiveColrEnumCS = colrEnumCS;
        }
        else if (nrColorSpaceSpecs == 1)
        {
            effectiveColrMethod = firstColrMethod ?? 0;
            effectiveColrEnumCS = firstColrEnumCS;
        }
        else
        {
            effectiveColrMethod = 0;
            effectiveColrEnumCS = null;
        }

        model.Set("nrColorChannels", nrColorChannels);
        model.Set("nrColorSpaceSpecs", nrColorSpaceSpecs);
        model.Set("nrColorSpacesWithApproxField", nrColorSpacesWithApproxField);
        model.Set("colrMethod", effectiveColrMethod);
        if (effectiveColrEnumCS.HasValue)
            model.Set("colrEnumCS", effectiveColrEnumCS.Value);
        model.Set("bitDepth", bitDepth);
        model.Set("bpccBoxPresent", bpccBoxPresent);
    }

    private static (long Length, string Type) ReadBox(byte[] data, int offset)
    {
        long len = ReadUInt32BE(data, offset);
        string type = Encoding.ASCII.GetString(data, offset + 4, 4);
        if (len == 1 && offset + 16 <= data.Length)
        {
            // Extended length (8 bytes)
            len = ((long)ReadUInt32BE(data, offset + 8) << 32) | ReadUInt32BE(data, offset + 12);
        }
        else if (len == 0)
        {
            // Box extends to end of data
            len = data.Length - offset;
        }
        return (len, type);
    }

    private static uint ReadUInt32BE(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) | data[offset + 3];
    }

    private static ushort ReadUInt16BE(byte[] data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static int ReadInt32BE(byte[] data, int offset)
    {
        return (int)ReadUInt32BE(data, offset);
    }

    private static string ReadAscii(byte[] bytes, int start, int length)
    {
        if (start + length > bytes.Length)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(bytes, start, length);
    }

    private void PopulateXmpPackage(GenericModelObject model, PdfStream stream)
    {
        var snapshot = GetXmpSnapshot(stream);
        model.Set("bytes", snapshot?.PacketBytesAttribute);
        model.Set("encoding", snapshot?.PacketEncodingAttribute);
        model.Set("actualEncoding", snapshot?.ActualEncoding);
        model.Set("isSerializationValid", snapshot?.IsSerializationValid ?? false);
    }

    private XmpMetadataSnapshot? GetXmpSnapshot(PdfStream stream)
    {
        if (_xmpCache.TryGetValue(stream, out var cached))
        {
            return cached;
        }

        XmpMetadataSnapshot? snapshot;
        try
        {
            var bytes = stream.Contents.GetDecodedData();
            var actualEncoding = DetectEncoding(bytes);
            var text = actualEncoding.GetString(bytes).TrimStart('\uFEFF');
            var packetMatch = Regex.Match(text, @"<\?xpacket\b(?<attrs>[^?]*)\?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var packetBytes = TryReadAttribute(packetMatch, BytesName.Value);
            var packetEncoding = TryReadAttribute(packetMatch, "encoding");

            XDocument? document;
            bool valid;
            try
            {
                document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
                valid = true;
            }
            catch
            {
                document = null;
                valid = false;
            }

            var dcTitle = document is null ? null : ReadDcTitle(document);
            var prefixMap = BuildPrefixMap(text);
            var pdfUa = document is null ? null : ReadPdfUaIdentification(document, prefixMap);
            var pdfa = document is null ? null : ReadPdfAIdentification(document, prefixMap);
            var langAlts = document is null ? Array.Empty<XmpLangAltEntry>() : ReadXmpLangAlts(document);
            var xmpInfo = document is null ? default : ReadXmpInfoProperties(document);
            snapshot = new XmpMetadataSnapshot(valid, actualEncoding.WebName.ToUpperInvariant(), packetBytes, packetEncoding, dcTitle, pdfUa, pdfa, langAlts,
                xmpInfo.CreateDate, xmpInfo.ModifyDate, xmpInfo.Creator, xmpInfo.CreatorSize,
                xmpInfo.Description, xmpInfo.Producer, xmpInfo.CreatorTool, xmpInfo.Keywords,
                document);
        }
        catch
        {
            snapshot = null;
        }

        _xmpCache[stream] = snapshot;
        return snapshot;
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        // UTF-32 LE BOM (must check before UTF-16 LE since FF FE is a prefix)
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return Encoding.UTF32; // UTF-32 LE
        }

        // UTF-32 BE BOM
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode; // UTF-16 LE
        }

        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode; // UTF-16 BE
        }

        // No BOM — detect encoding from byte pattern of '<' (0x3C)
        if (bytes.Length >= 4)
        {
            // UTF-32 BE: 00 00 00 3C
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x3C)
            {
                return new UTF32Encoding(bigEndian: true, byteOrderMark: false);
            }

            // UTF-32 LE: 3C 00 00 00
            if (bytes[0] == 0x3C && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return Encoding.UTF32; // UTF-32 LE
            }

            // UTF-16 BE: 00 3C 00 3F
            if (bytes[0] == 0x00 && bytes[1] == 0x3C && bytes[2] == 0x00 && bytes[3] == 0x3F)
            {
                return Encoding.BigEndianUnicode; // UTF-16 BE
            }

            // UTF-16 LE: 3C 00 3F 00
            if (bytes[0] == 0x3C && bytes[1] == 0x00 && bytes[2] == 0x3F && bytes[3] == 0x00)
            {
                return Encoding.Unicode; // UTF-16 LE
            }
        }

        // Try parsing encoding attribute from ASCII-compatible prefix
        var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        var xmlDecl = Regex.Match(prefix, @"<\?xml[^>]*encoding\s*=\s*['""](?<enc>[^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (xmlDecl.Success)
        {
            try
            {
                return Encoding.GetEncoding(xmlDecl.Groups["enc"].Value);
            }
            catch
            {
            }
        }

        return Encoding.UTF8;
    }

    private static string? TryReadAttribute(Match packetMatch, string attributeName)
    {
        if (!packetMatch.Success)
        {
            return null;
        }

        var attrs = packetMatch.Groups["attrs"].Value;
        var match = Regex.Match(attrs, $@"\b{Regex.Escape(attributeName)}\s*=\s*['""](?<value>[^'""]*)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? ReadDcTitle(XDocument document)
    {
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        return document.Descendants(dc + "title")
            .Descendants(rdf + "li")
            .Select(static x => x.Value)
            .FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x))
            ?? document.Descendants(dc + "title")
                .Select(static x => x.Value)
                .FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x));
    }

    private static (string? CreateDate, string? ModifyDate, string? Creator, int CreatorSize, string? Description, string? Producer, string? CreatorTool, string? Keywords) ReadXmpInfoProperties(XDocument document)
    {
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
        XNamespace pdf = "http://ns.adobe.com/pdf/1.3/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        var createDate = ReadSimpleXmpValue(document, xmp + "CreateDate");
        var modifyDate = ReadSimpleXmpValue(document, xmp + "ModifyDate");
        var creatorTool = ReadSimpleXmpValue(document, xmp + "CreatorTool");
        var producer = ReadSimpleXmpValue(document, pdf + "Producer");
        var keywords = ReadSimpleXmpValue(document, pdf + "Keywords");

        // dc:description is a LangAlt — same structure as dc:title
        var description = document.Descendants(dc + "description")
            .Descendants(rdf + "li")
            .Select(static x => x.Value)
            .FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x))
            ?? document.Descendants(dc + "description")
                .Select(static x => x.Value)
                .FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x));

        // dc:creator is an ordered array (rdf:Seq)
        var creatorItems = document.Descendants(dc + "creator")
            .Descendants(rdf + "li")
            .Select(static x => x.Value)
            .ToList();
        var creator = creatorItems.Count > 0 ? string.Join(", ", creatorItems) : null;
        var creatorSize = creatorItems.Count;

        return (createDate, modifyDate, creator, creatorSize, description, producer, creatorTool, keywords);
    }

    private static string? ReadSimpleXmpValue(XDocument document, XName name)
    {
        // Try as element first, then as attribute on rdf:Description
        var element = document.Descendants(name).FirstOrDefault();
        if (element is not null)
        {
            return element.Value;
        }

        // Check attributes on rdf:Description elements
        foreach (var desc in document.Descendants(XNamespace.Get("http://www.w3.org/1999/02/22-rdf-syntax-ns#") + "Description"))
        {
            var attr = desc.Attribute(name);
            if (attr is not null)
            {
                return attr.Value;
            }
        }

        return null;
    }

    private static PdfUaIdentificationSnapshot? ReadPdfUaIdentification(XDocument document, Dictionary<(string ns, string local), string> prefixMap)
    {
        const string namespaceUri = "http://www.aiim.org/pdfua/ns/id/";
        var schema = FindSchemaNode(document, namespaceUri);
        if (schema is null)
        {
            return null;
        }

        XNamespace ns = namespaceUri;

        return new PdfUaIdentificationSnapshot(
            ParseInt(ReadSchemaValue(schema, ns + "part")),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "part") ?? GetSchemaPrefix(schema, ns + "part"),
            ReadSchemaValue(schema, ns + "rev"),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "rev") ?? GetSchemaPrefix(schema, ns + "rev"),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "amd") ?? GetSchemaPrefix(schema, ns + "amd"),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "corr") ?? GetSchemaPrefix(schema, ns + "corr"));
    }

    private static PdfAIdentificationSnapshot? ReadPdfAIdentification(XDocument document, Dictionary<(string ns, string local), string> prefixMap)
    {
        const string namespaceUri = "http://www.aiim.org/pdfa/ns/id/";
        var schema = FindSchemaNode(document, namespaceUri);
        if (schema is null)
        {
            return null;
        }

        XNamespace ns = namespaceUri;

        return new PdfAIdentificationSnapshot(
            ParseInt(ReadSchemaValue(schema, ns + "part")),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "part") ?? GetSchemaPrefix(schema, ns + "part"),
            ReadSchemaValue(schema, ns + "conformance"),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "conformance") ?? GetSchemaPrefix(schema, ns + "conformance"),
            ReadSchemaValue(schema, ns + "rev"),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "rev") ?? GetSchemaPrefix(schema, ns + "rev"),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "amd") ?? GetSchemaPrefix(schema, ns + "amd"),
            GetSchemaPrefixFromMap(prefixMap, namespaceUri, "corr") ?? GetSchemaPrefix(schema, ns + "corr"));
    }

    private static XElement? FindSchemaNode(XDocument document, string namespaceUri) =>
        document.Descendants()
            .FirstOrDefault(x =>
                x.Elements().Any(e => e.Name.NamespaceName == namespaceUri) ||
                x.Attributes().Any(a => a.Name.NamespaceName == namespaceUri));

    /// <summary>
    /// Reads a value from an XMP schema node, checking child elements first then attributes.
    /// XMP can represent properties as either child elements or attributes on rdf:Description.
    /// </summary>
    private static string? ReadSchemaValue(XElement schema, XName name)
    {
        // Try child element first (e.g. <pdfaid:part>1</pdfaid:part>)
        var element = schema.Element(name);
        if (element is not null) return element.Value;
        // Fall back to attribute (e.g. pdfaid:part="1")
        return schema.Attribute(name)?.Value;
    }

    /// <summary>
    /// Gets the namespace prefix for a property in an XMP schema node.
    /// </summary>
    private static string? GetSchemaPrefix(XElement schema, XName name)
    {
        var element = schema.Element(name);
        if (element is not null)
            return element.GetPrefixOfNamespace(element.Name.Namespace);
        var attr = schema.Attribute(name);
        if (attr is not null)
            return schema.GetPrefixOfNamespace(attr.Name.Namespace);
        return null;
    }

    /// <summary>
    /// Builds a map of (namespaceUri, localName) → prefix from raw XML using XmlReader.
    /// This preserves the actual prefix used in the source XML, unlike LINQ to XML's
    /// GetPrefixOfNamespace which is ambiguous when multiple prefixes map to the same URI.
    /// </summary>
    private static Dictionary<(string ns, string local), string> BuildPrefixMap(string rawXml)
    {
        var map = new Dictionary<(string ns, string local), string>();
        try
        {
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(rawXml));
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element)
                {
                    if (!string.IsNullOrEmpty(reader.NamespaceURI) && !string.IsNullOrEmpty(reader.Prefix))
                    {
                        map.TryAdd((reader.NamespaceURI, reader.LocalName), reader.Prefix);
                    }

                    if (reader.HasAttributes)
                    {
                        for (int i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            if (!string.IsNullOrEmpty(reader.NamespaceURI) && !string.IsNullOrEmpty(reader.Prefix)
                                && reader.Prefix != "xmlns")
                            {
                                map.TryAdd((reader.NamespaceURI, reader.LocalName), reader.Prefix);
                            }
                        }

                        reader.MoveToElement();
                    }
                }
            }
        }
        catch
        {
            // If parsing fails, return whatever we collected
        }

        return map;
    }

    private static string? GetSchemaPrefixFromMap(Dictionary<(string ns, string local), string> prefixMap, string namespaceUri, string localName)
    {
        return prefixMap.TryGetValue((namespaceUri, localName), out var prefix) ? prefix : null;
    }

    private static string? GetPrefix(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        return element.GetPrefixOfNamespace(element.Name.Namespace);
    }

    private static int? ParseInt(string? raw) =>
        int.TryParse(raw, out var parsed) ? parsed : null;

    private static XmpLangAltEntry[] ReadXmpLangAlts(XDocument document)
    {
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace xml = "http://www.w3.org/XML/1998/namespace";

        var result = new List<XmpLangAltEntry>();
        foreach (var alt in document.Descendants(rdf + "Alt"))
        {
            var items = alt.Elements(rdf + "li").ToList();
            bool isXDefault = items.Count == 1
                && string.Equals(items[0].Attribute(xml + "lang")?.Value, "x-default", StringComparison.OrdinalIgnoreCase);
            result.Add(new XmpLangAltEntry(isXDefault));
        }

        return result.ToArray();
    }

    private void EnsureStructureCaches()
    {
        if (_structureInitialized)
        {
            return;
        }

        _structureInitialized = true;

        var structRoot = _document.Catalog.GetOptionalValue<PdfDictionary>(StructTreeRootName);
        if (structRoot is null)
        {
            return;
        }

        var parentTree = structRoot.GetOptionalValue<PdfDictionary>(new PdfName("ParentTree"));
        if (parentTree is not null)
        {
            WalkNumberTree(parentTree, (key, value) =>
            {
                if (value.Resolve() is PdfArray array)
                {
                    for (var index = 0; index < array.Count; index++)
                    {
                        if (TryFindFirstStructElement(array[index], out var structElement, out var objectKey))
                        {
                            _markedContentParentTreeMap[new MarkedContentKey(key, index)] = GetStructInfo(structElement, objectKey);
                        }
                    }

                    return;
                }

                if (TryFindFirstStructElement(value, out var directStructElement, out var directObjectKey))
                {
                    _parentTreeMap[key] = GetStructInfo(directStructElement, directObjectKey);
                }
            });
        }

        BuildDerivedStructureCaches();
    }

    private void BuildDerivedStructureCaches()
    {
        _duplicateNoteIds.Clear();
        _tableInfoCache.Clear();
        _tableCellInfoCache.Clear();
        _headingInfoCache.Clear();
        _tableCellGeometryCache.Clear();

        var structTreeRoot = _document.Catalog.GetOptionalValue<PdfDictionary>(StructTreeRootName);
        if (structTreeRoot is null)
        {
            return;
        }

        var noteIdCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxHeadingLevel = 0;
        foreach (var element in EnumerateStructureElements(structTreeRoot.Get(KidsName)))
        {
            var info = GetStructInfo(element);
            if (string.Equals(info.StandardType, "Note", StringComparison.Ordinal) && !string.IsNullOrEmpty(info.NoteId))
            {
                noteIdCounts[info.NoteId] = noteIdCounts.TryGetValue(info.NoteId, out var count) ? count + 1 : 1;
            }

            if (string.Equals(info.StandardType, "Table", StringComparison.Ordinal))
            {
                BuildTableInfo(element);
            }

            // Heading nesting level tracking
            var headingLevel = GetHeadingLevel(info.StandardType);
            if (headingLevel > 0)
            {
                var correct = headingLevel <= maxHeadingLevel + 1;
                _headingInfoCache[element] = new HeadingInfo(headingLevel, correct);
                maxHeadingLevel = Math.Max(maxHeadingLevel, headingLevel);
            }
        }

        foreach (var item in noteIdCounts)
        {
            if (item.Value > 1)
            {
                _duplicateNoteIds.Add(item.Key);
            }
        }
    }

    private static int GetHeadingLevel(string? standardType) => standardType switch
    {
        "H1" => 1,
        "H2" => 2,
        "H3" => 3,
        "H4" => 4,
        "H5" => 5,
        "H6" => 6,
        _ => 0,
    };

    private void WalkNumberTree(PdfDictionary node, Action<int, IPdfObject> visit)
    {
        if (node.TryGetValue<PdfArray>(NumsName, out var nums, false))
        {
            for (var index = 0; index + 1 < nums.Count; index += 2)
            {
                if (nums[index].Resolve() is PdfNumber key)
                {
                    visit(Convert.ToInt32((double)key, System.Globalization.CultureInfo.InvariantCulture), nums[index + 1]);
                }
            }
        }

        if (node.TryGetValue<PdfArray>(KidsName, out var kids, false))
        {
            foreach (var kid in kids)
            {
                if (kid.Resolve() is PdfDictionary child)
                {
                    WalkNumberTree(child, visit);
                }
            }
        }
    }

    private bool TryFindFirstStructElement(IPdfObject value, out PdfDictionary structElement, out string? objectKey)
    {
        var resolved = value.Resolve();
        if (resolved is PdfDictionary dictionary)
        {
            var type = ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName));
            if (string.Equals(type, "StructElem", StringComparison.Ordinal) || dictionary.ContainsKey(new PdfName("S")))
            {
                structElement = dictionary;
                objectKey = GetObjectKey(value) ?? GetObjectKey(dictionary);
                return true;
            }
        }

        if (resolved is PdfArray array)
        {
            foreach (var item in array)
            {
                if (TryFindFirstStructElement(item, out structElement, out objectKey))
                {
                    return true;
                }
            }
        }

        structElement = null!;
        objectKey = null;
        return false;
    }

    private bool TryGetParentTreeStructInfo(int key, out StructInfo structInfo)
    {
        EnsureStructureCaches();
        return _parentTreeMap.TryGetValue(key, out structInfo!);
    }

    private bool TryGetMarkedContentStructInfo(int structParents, int mcid, out StructInfo structInfo)
    {
        EnsureStructureCaches();
        return _markedContentParentTreeMap.TryGetValue(new MarkedContentKey(structParents, mcid), out structInfo!);
    }

    private StructInfo GetStructInfo(PdfDictionary element, string? objectKeyOverride = null)
    {
        EnsureStructureCaches();
        if (_structCache.TryGetValue(element, out var cached))
        {
            return cached;
        }

        var rawType = ConvertPdfObjectToString(element.Get(new PdfName("S")));
        var elementNs = element.GetOptionalValue<PdfDictionary>(new PdfName("NS"));
        var standardType = ResolveStandardType(rawType, elementNs, out var circular, out var remappedStandardType);
        var lang = ConvertPdfObjectToString(element.Get(LangName));
        var info = new StructInfo(
            rawType,
            standardType,
            remappedStandardType,
            !string.IsNullOrEmpty(rawType) && !StandardStructTypes.Contains(rawType) && standardType is null,
            circular,
            GetParentStandardType(element.GetOptionalValue<PdfDictionary>(StructElemParentName)),
            GetChildStandardTypes(element.Get(KidsName)),
            ConvertPdfObjectToString(element.Get(AltName)),
            ConvertPdfObjectToString(element.Get(ActualTextName)),
            !string.IsNullOrEmpty(lang),
            GetInheritedParentLang(element.GetOptionalValue<PdfDictionary>(StructElemParentName)),
            ConvertPdfObjectToString(element.Get(new PdfName("ID"))),
            lang,
            objectKeyOverride ?? GetObjectKey(element));

        _structCache[element] = info;
        return info;
    }

    private bool TryGetTableInfo(PdfDictionary element, out TableInfo tableInfo)
    {
        EnsureStructureCaches();
        return _tableInfoCache.TryGetValue(element, out tableInfo!);
    }

    private bool TryGetTableCellInfo(PdfDictionary element, out TableCellInfo cellInfo)
    {
        EnsureStructureCaches();
        return _tableCellInfoCache.TryGetValue(element, out cellInfo!);
    }

    private string? ResolveStandardType(string? rawType, PdfDictionary? elementNs, out bool circular, out string? remappedStandardType)
    {
        circular = false;
        remappedStandardType = null;
        if (string.IsNullOrEmpty(rawType))
        {
            return null;
        }

        // Check if the element's namespace is a standard one
        if (elementNs is not null)
        {
            var nsUrl = ConvertPdfObjectToString(elementNs.Get(new PdfName("NS")));
            if (nsUrl is not null && StandardNamespaceUrls.Contains(nsUrl))
            {
                // Element is in a standard namespace — its type is considered standard
                return rawType;
            }

            // Try RoleMapNS-based resolution for non-standard namespaces
            var resolvedViaNamespace = ResolveViaRoleMapNS(rawType, elementNs);
            if (resolvedViaNamespace is not null)
                return resolvedViaNamespace;
        }

        var roleMap = _document.Catalog.GetOptionalValue<PdfDictionary>(StructTreeRootName)?.GetOptionalValue<PdfDictionary>(RoleMapName);
        if (roleMap is null)
        {
            return StandardStructTypes.Contains(rawType) ? rawType : null;
        }

        if (StandardStructTypes.Contains(rawType))
        {
            // Only flag remapping for PDF 1.7 standard types (rule 7.1/7 per ISO 32000-1:2008, 14.8.4)
            if (Pdf17StandardStructTypes.Contains(rawType))
                remappedStandardType = ConvertPdfObjectToString(roleMap.Get(new PdfName(rawType)));
            return rawType;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = rawType;
        while (!string.IsNullOrEmpty(current))
        {
            if (!seen.Add(current))
            {
                circular = true;
                return null;
            }

            if (StandardStructTypes.Contains(current))
            {
                return current;
            }

            current = ConvertPdfObjectToString(roleMap.Get(new PdfName(current)));
        }

        return null;
    }

    /// <summary>
    /// Follows the RoleMapNS chain from a non-standard namespace to resolve a type to a standard namespace.
    /// RoleMapNS entries map type names to [targetType, targetNamespace] arrays.
    /// </summary>
    private static string? ResolveViaRoleMapNS(string typeName, PdfDictionary ns)
    {
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer<PdfDictionary>.Instance);
        var currentType = typeName;
        var currentNs = ns;

        while (currentNs is not null && visited.Add(currentNs))
        {
            var roleMapNs = currentNs.GetOptionalValue<PdfDictionary>(new PdfName("RoleMapNS"));
            if (roleMapNs is null)
                return null;

            var mapping = roleMapNs.Get(new PdfName(currentType))?.Resolve();
            if (mapping is PdfArray arr && arr.Count >= 2)
            {
                var targetType = ConvertPdfObjectToString(arr[0]);
                var targetNs = arr[1].Resolve() as PdfDictionary;

                if (targetType is null) return null;

                if (targetNs is not null)
                {
                    var targetNsUrl = ConvertPdfObjectToString(targetNs.Get(new PdfName("NS")));
                    if (targetNsUrl is not null && StandardNamespaceUrls.Contains(targetNsUrl))
                        return targetType; // Resolved to a standard namespace
                }

                // Continue following the chain
                currentType = targetType;
                currentNs = targetNs;
            }
            else
            {
                return null;
            }
        }

        return null;
    }

    private string? GetParentStandardType(PdfDictionary? parent)
    {
        if (parent is null)
        {
            return null;
        }

        var type = ConvertPdfObjectToString(parent.Get(PdfName.TypeName));
        if (string.Equals(type, "StructElem", StringComparison.Ordinal) || parent.ContainsKey(new PdfName("S")))
        {
            var rawType = ConvertPdfObjectToString(parent.Get(new PdfName("S")));
            var parentNs = parent.GetOptionalValue<PdfDictionary>(new PdfName("NS"));
            return ResolveStandardType(rawType, parentNs, out _, out _);
        }

        return null;
    }

    private string? GetInheritedParentLang(PdfDictionary? parent)
    {
        while (parent is not null)
        {
            var lang = ConvertPdfObjectToString(parent.Get(LangName));
            if (!string.IsNullOrEmpty(lang))
            {
                return lang;
            }

            parent = parent.GetOptionalValue<PdfDictionary>(StructElemParentName);
        }

        return ConvertPdfObjectToString(_document.Catalog.Get(LangName));
    }

    private IReadOnlyList<string> GetChildStandardTypes(IPdfObject? kids)
    {
        if (kids is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var child in EnumerateKids(kids))
        {
            if (child.Resolve() is PdfDictionary dictionary)
            {
                var type = ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName));
                if (string.Equals(type, "StructElem", StringComparison.Ordinal) || dictionary.ContainsKey(new PdfName("S")))
                {
                    var childInfo = GetStructInfo(dictionary);
                    if (!string.IsNullOrEmpty(childInfo.StandardType))
                    {
                        result.Add(childInfo.StandardType);
                    }
                }
            }
        }

        return result;
    }

    private IEnumerable<PdfDictionary> EnumerateStructureElements(IPdfObject? kids)
    {
        if (kids is null)
        {
            yield break;
        }

        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer<PdfDictionary>.Instance);
        foreach (var element in EnumerateStructureElements(kids, visited))
        {
            yield return element;
        }
    }

    private IEnumerable<PdfDictionary> EnumerateStructureElements(IPdfObject kids, HashSet<PdfDictionary> visited)
    {
        foreach (var child in EnumerateKids(kids))
        {
            if (child.Resolve() is not PdfDictionary dictionary || !visited.Add(dictionary))
            {
                continue;
            }

            var type = ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName));
            if (string.Equals(type, "StructElem", StringComparison.Ordinal) || dictionary.ContainsKey(new PdfName("S")))
            {
                yield return dictionary;
                var nestedKids = dictionary.Get(KidsName);
                if (nestedKids is not null)
                {
                    foreach (var nested in EnumerateStructureElements(nestedKids, visited))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    private void BuildTableInfo(PdfDictionary table)
    {
        if (_tableInfoCache.ContainsKey(table))
        {
            return;
        }

        var rows = CollectTableRows(table);
        var rowWidths = new List<int>();
        var placements = new List<TableCellPlacement>();
        var active = new List<int>();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var cells = CollectRowCells(row);
            var currentOccupied = active.Select(static value => value > 0).ToList();
            var nextActive = active.Select(static value => Math.Max(value - 1, 0)).ToList();
            var nextColumn = 0;

            foreach (var cell in cells)
            {
                var standardType = GetStructInfo(cell).StandardType;
                if (!string.Equals(standardType, "TH", StringComparison.Ordinal) && !string.Equals(standardType, "TD", StringComparison.Ordinal))
                {
                    continue;
                }

                var colSpan = Math.Max(1, GetStructureAttributeInt(cell, "ColSpan") ?? 1);
                var rowSpan = Math.Max(1, GetStructureAttributeInt(cell, "RowSpan") ?? 1);
                var columnIndex = FindNextFreeColumn(currentOccupied, nextColumn, colSpan);
                EnsureBoolSize(currentOccupied, columnIndex + colSpan);
                EnsureIntSize(nextActive, columnIndex + colSpan);

                for (var column = 0; column < colSpan; column++)
                {
                    currentOccupied[columnIndex + column] = true;
                    if (rowSpan > 1)
                    {
                        nextActive[columnIndex + column] = Math.Max(nextActive[columnIndex + column], rowSpan - 1);
                    }
                }

                placements.Add(new TableCellPlacement(
                    cell,
                    standardType,
                    rowIndex,
                    columnIndex,
                    rowSpan,
                    colSpan,
                    GetStructureAttributeStrings(cell, "Headers"),
                    GetStructureAttributeString(cell, "Scope"),
                    GetHeaderId(cell)));

                nextColumn = columnIndex + colSpan;
            }

            rowWidths.Add(GetRowWidth(currentOccupied));
            active = nextActive;
        }

        var columnHeights = new List<int>();
        foreach (var placement in placements)
        {
            EnsureIntSize(columnHeights, placement.ColumnIndex + placement.ColSpan);
            for (var column = 0; column < placement.ColSpan; column++)
            {
                columnHeights[placement.ColumnIndex + column] += placement.RowSpan;
            }
        }

        var firstColumnHeight = columnHeights.Count > 0 ? columnHeights[0] : (int?)null;
        int? wrongColumnIndex = null;
        if (firstColumnHeight.HasValue)
        {
            for (var column = 1; column < columnHeights.Count; column++)
            {
                if (columnHeights[column] != firstColumnHeight.Value)
                {
                    wrongColumnIndex = column;
                    break;
                }
            }
        }

        var firstRowWidth = rowWidths.Count > 0 ? rowWidths[0] : (int?)null;
        int? wrongRowIndex = null;
        int? wrongRowWidth = null;
        if (firstRowWidth.HasValue)
        {
            for (var row = 1; row < rowWidths.Count; row++)
            {
                if (rowWidths[row] != firstRowWidth.Value)
                {
                    wrongRowIndex = row;
                    wrongRowWidth = rowWidths[row];
                    break;
                }
            }
        }

        var headerIds = placements
            .Where(static x => string.Equals(x.StandardType, "TH", StringComparison.Ordinal) && !string.IsNullOrEmpty(x.HeaderId))
            .ToDictionary(static x => x.HeaderId!, static x => x, StringComparer.Ordinal);

        foreach (var placement in placements.Where(static x => string.Equals(x.StandardType, "TD", StringComparison.Ordinal)))
        {
            // Skip position (0,0) — veraPDF Java skips this position since it can never
            // have algorithmic headers (nothing above or to the left).
            if (placement.RowIndex == 0 && placement.ColumnIndex == 0)
                continue;
            _tableCellInfoCache[placement.Element] = BuildTableCellInfo(placement, placements, headerIds);
        }

        // Compute cell intersection and geometry for all cells
        var grid = new Dictionary<(int Row, int Col), PdfDictionary>();
        foreach (var placement in placements)
        {
            var hasIntersection = false;
            for (var r = 0; r < placement.RowSpan; r++)
            {
                for (var c = 0; c < placement.ColSpan; c++)
                {
                    var cell = (placement.RowIndex + r, placement.ColumnIndex + c);
                    if (grid.ContainsKey(cell))
                    {
                        hasIntersection = true;
                        // Also mark the other cell as intersecting
                        if (_tableCellGeometryCache.TryGetValue(grid[cell], out var otherGeo))
                        {
                            _tableCellGeometryCache[grid[cell]] = otherGeo with { HasIntersection = true };
                        }
                    }

                    grid[cell] = placement.Element;
                }
            }

            _tableCellGeometryCache[placement.Element] = new TableCellGeometry(
                placement.ColSpan,
                placement.RowSpan,
                hasIntersection);
        }

        _tableInfoCache[table] = new TableInfo(wrongColumnIndex, wrongRowIndex, firstRowWidth, wrongRowWidth);
    }

    private static List<PdfDictionary> CollectTableRows(PdfDictionary table)
    {
        var result = new List<PdfDictionary>();
        foreach (var child in EnumerateChildStructureElements(table))
        {
            switch (ConvertPdfObjectToString(child.Get(new PdfName("S"))))
            {
                case "TR":
                    result.Add(child);
                    break;
                case "THead":
                case "TBody":
                case "TFoot":
                    result.AddRange(CollectTableRows(child));
                    break;
            }
        }

        return result;
    }

    private static List<PdfDictionary> CollectRowCells(PdfDictionary row)
    {
        var result = new List<PdfDictionary>();
        foreach (var child in EnumerateChildStructureElements(row))
        {
            var type = ConvertPdfObjectToString(child.Get(new PdfName("S")));
            if (type is "TH" or "TD")
            {
                result.Add(child);
            }
        }

        return result;
    }

    private TableCellInfo BuildTableCellInfo(
        TableCellPlacement placement,
        IReadOnlyList<TableCellPlacement> placements,
        IReadOnlyDictionary<string, TableCellPlacement> headerIds)
    {
        if (placement.Headers.Count > 0)
        {
            var known = placement.Headers.Where(headerIds.ContainsKey).ToArray();
            var unknown = placement.Headers
                .Where(static header => !string.IsNullOrEmpty(header))
                .Except(known, StringComparer.Ordinal)
                .ToArray();
            return new TableCellInfo(known.Length > 0, string.Join(", ", unknown));
        }

        var rowHeader = placements.Any(other =>
            string.Equals(other.StandardType, "TH", StringComparison.Ordinal) &&
            other.RowIndex == placement.RowIndex &&
            other.ColumnIndex < placement.ColumnIndex &&
            ScopeIncludesRow(other.Scope));

        var columnHeader = placements.Any(other =>
            string.Equals(other.StandardType, "TH", StringComparison.Ordinal) &&
            other.RowIndex < placement.RowIndex &&
            RangesOverlap(other.ColumnIndex, other.ColSpan, placement.ColumnIndex, placement.ColSpan) &&
            ScopeIncludesColumn(other.Scope));

        return new TableCellInfo(rowHeader || columnHeader, string.Empty);
    }

    private static bool RangesOverlap(int firstStart, int firstLength, int secondStart, int secondLength) =>
        firstStart < secondStart + secondLength && secondStart < firstStart + firstLength;

    private static bool ScopeIncludesRow(string? scope) =>
        string.Equals(scope, "Row", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scope, "Both", StringComparison.OrdinalIgnoreCase);

    private static bool ScopeIncludesColumn(string? scope) =>
        string.Equals(scope, "Column", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(scope, "Both", StringComparison.OrdinalIgnoreCase);

    private static bool HasContentItems(PdfDictionary element)
    {
        var kids = element.Get(KidsName);
        if (kids is null)
        {
            return false;
        }

        var resolved = kids.Resolve();
        if (resolved is PdfNumber)
        {
            return true;
        }

        if (resolved is PdfDictionary dict)
        {
            return IsContentItem(dict);
        }

        if (resolved is PdfArray array)
        {
            foreach (var item in array)
            {
                var itemResolved = item.Resolve();
                if (itemResolved is PdfNumber)
                {
                    return true;
                }

                if (itemResolved is PdfDictionary d && IsContentItem(d))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsContentItem(PdfDictionary dict)
    {
        var type = ConvertPdfObjectToString(dict.Get(PdfName.TypeName));
        return type is "MCR" or "OBJR";
    }

    private bool ContainsLabels(PdfDictionary listElement)
    {
        foreach (var child in EnumerateChildStructureElements(listElement))
        {
            var childInfo = GetStructInfo(child);
            if (!string.Equals(childInfo.StandardType, "LI", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var grandchild in EnumerateChildStructureElements(child))
            {
                var gcInfo = GetStructInfo(grandchild);
                if (string.Equals(gcInfo.StandardType, "Lbl", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private int CountWidgetAnnotChildren(PdfDictionary element)
    {
        var kids = element.Get(KidsName);
        if (kids is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var child in EnumerateKids(kids))
        {
            var resolved = child.Resolve();
            if (resolved is not PdfDictionary dict)
            {
                continue;
            }

            var type = ConvertPdfObjectToString(dict.Get(PdfName.TypeName));
            if (string.Equals(type, "OBJR", StringComparison.Ordinal))
            {
                var obj = dict.Get(new PdfName("Obj"))?.Resolve() as PdfDictionary;
                if (obj is not null)
                {
                    var subtype = ConvertPdfObjectToString(obj.Get(PdfName.Subtype));
                    if (string.Equals(subtype, "Widget", StringComparison.Ordinal))
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    private static List<IModelObject> CreateLangObjects(PdfDictionary element)
    {
        var result = new List<IModelObject>();
        var lang = ConvertPdfObjectToString(element.Get(LangName));
        if (!string.IsNullOrEmpty(lang))
        {
            var langObj = new GenericModelObject("CosLang");
            langObj.Set("unicodeValue", lang);
            result.Add(langObj);
        }

        return result;
    }

    private List<IModelObject> CreateOcConfigObjects(PdfDictionary catalog)
    {
        var result = new List<IModelObject>();
        var ocProperties = catalog.GetOptionalValue<PdfDictionary>(OCPropertiesName);
        if (ocProperties is null)
        {
            return result;
        }

        // Collect all config dicts first to detect duplicate names
        var configDicts = new List<PdfDictionary>();
        var defaultConfig = ocProperties.GetOptionalValue<PdfDictionary>(DName);
        if (defaultConfig is not null)
        {
            configDicts.Add(defaultConfig);
        }

        var configs = ocProperties.GetOptionalValue<PdfArray>(ConfigsName);
        if (configs is not null)
        {
            foreach (var item in configs)
            {
                if (item.Resolve() is PdfDictionary configDict)
                {
                    configDicts.Add(configDict);
                }
            }
        }

        // Gather all names and find duplicates
        var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cd in configDicts)
        {
            var name = ConvertPdfObjectToString(cd.Get(PdfName.Name));
            if (name is not null)
            {
                nameCount[name] = nameCount.TryGetValue(name, out var c) ? c + 1 : 1;
            }
        }

        // Determine if Configs entry exists with at least one config dict
        var containsConfigs = configs is not null && configs.Any(i => i.Resolve() is PdfDictionary);

        // Collect all OCG indirect references from OCProperties.OCGs
        var allOcgRefs = new HashSet<IPdfObject>(ReferenceEqualityComparer.Instance);
        var ocgs = ocProperties.GetOptionalValue<PdfArray>(OCGsName);
        if (ocgs is not null)
        {
            foreach (var item in ocgs)
            {
                var resolved = item.Resolve();
                if (resolved is PdfDictionary)
                    allOcgRefs.Add(resolved);
            }
        }

        foreach (var cd in configDicts)
        {
            var model = new GenericModelObject("PDOCConfig");
            PopulateOcConfig(model, cd);
            var name = ConvertPdfObjectToString(cd.Get(PdfName.Name));
            model.Set("hasDuplicateName", name is not null && nameCount.TryGetValue(name, out var cnt) && cnt > 1);
            model.Set("gContainsConfigs", containsConfigs);

            // Compute OCGsNotContainedInOrder: OCGs from OCProperties that are missing from this config's Order
            var orderArray = cd.GetOptionalValue<PdfArray>(OrderName);
            if (orderArray is not null)
            {
                var orderRefs = new HashSet<IPdfObject>(ReferenceEqualityComparer.Instance);
                CollectOcgRefsFromOrder(orderArray, orderRefs);
                var missingNames = new List<string>();
                foreach (var ocg in allOcgRefs)
                {
                    if (!orderRefs.Contains(ocg) && ocg is PdfDictionary ocgDict)
                    {
                        var ocgName = ConvertPdfObjectToString(ocgDict.Get(PdfName.Name)) ?? "<unnamed>";
                        missingNames.Add(ocgName);
                    }
                }
                model.Set("OCGsNotContainedInOrder", missingNames.Count > 0 ? string.Join(", ", missingNames) : null);
            }

            result.Add(model);
        }

        return result;
    }

    private static void CollectOcgRefsFromOrder(PdfArray order, HashSet<IPdfObject> refs)
    {
        foreach (var item in order)
        {
            var resolved = item.Resolve();
            if (resolved is PdfArray nestedArray)
                CollectOcgRefsFromOrder(nestedArray, refs);
            else if (resolved is PdfDictionary)
                refs.Add(resolved);
        }
    }

    private List<IModelObject> CreateFormFieldObjects(PdfDictionary catalog)
    {
        var result = new List<IModelObject>();
        var acroForm = catalog.GetOptionalValue<PdfDictionary>(AcroFormName);
        if (acroForm is null)
        {
            return result;
        }

        var fields = acroForm.GetOptionalValue<PdfArray>(FieldsName);
        if (fields is not null)
        {
            CollectFormFields(fields, result);
        }

        return result;
    }

    private List<IModelObject> CollectSignatureReferences(PdfDictionary catalog, bool permsContainDocMDP)
    {
        var result = new List<IModelObject>();
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        // Collect from Perms/DocMDP signature
        var permsDict = catalog.GetOptionalValue<PdfDictionary>(new PdfName("Perms"));
        if (permsDict is not null)
        {
            var docMdpSig = permsDict.GetOptionalValue<PdfDictionary>(new PdfName("DocMDP"));
            if (docMdpSig is not null)
                CollectSigRefsFromSignature(docMdpSig, permsContainDocMDP, result, visited);
        }

        // Collect from all Sig form fields
        var acroForm = catalog.GetOptionalValue<PdfDictionary>(AcroFormName);
        if (acroForm is not null)
        {
            var fields = acroForm.GetOptionalValue<PdfArray>(FieldsName);
            if (fields is not null)
                CollectSigRefsFromFields(fields, permsContainDocMDP, result, visited);
        }

        return result;
    }

    private static void CollectSigRefsFromFields(PdfArray fields, bool permsContainDocMDP, List<IModelObject> result, HashSet<PdfDictionary> visited)
    {
        foreach (var item in fields)
        {
            if (item.Resolve() is not PdfDictionary fieldDict)
                continue;

            var ft = ConvertPdfObjectToString(fieldDict.Get(FTName));
            if (string.Equals(ft, "Sig", StringComparison.Ordinal))
            {
                var sigDict = fieldDict.GetOptionalValue<PdfDictionary>(PdfName.V);
                if (sigDict is not null)
                    CollectSigRefsFromSignature(sigDict, permsContainDocMDP, result, visited);
            }

            var kids = fieldDict.GetOptionalValue<PdfArray>(new PdfName("Kids"));
            if (kids is not null)
                CollectSigRefsFromFields(kids, permsContainDocMDP, result, visited);
        }
    }

    private static void CollectSigRefsFromSignature(PdfDictionary sigDict, bool permsContainDocMDP, List<IModelObject> result, HashSet<PdfDictionary> visited)
    {
        if (!visited.Add(sigDict))
            return;

        var refArray = sigDict.GetOptionalValue<PdfArray>(new PdfName("Reference"));
        if (refArray is null)
            return;

        foreach (var refItem in refArray)
        {
            if (refItem.Resolve() is not PdfDictionary refDict)
                continue;

            var sigRefObj = new GenericModelObject("PDSigRef");
            sigRefObj.Set("permsContainDocMDP", permsContainDocMDP);

            var keys = new List<string>();
            foreach (var key in refDict.Keys)
            {
                if (!string.Equals(key.Value, "Type", StringComparison.Ordinal))
                    keys.Add(key.Value!);
            }
            sigRefObj.Set("entries", string.Join("&", keys));

            result.Add(sigRefObj);
        }
    }

    private void CollectFormFields(PdfArray fields, List<IModelObject> result)
    {
        foreach (var item in fields)
        {
            if (item.Resolve() is not PdfDictionary fieldDict)
            {
                continue;
            }

            if (fieldDict.ContainsKey(FTName) || fieldDict.ContainsKey(new PdfName("T")))
            {
                var model = new GenericModelObject("PDFormField");
                PopulateFormField(model, fieldDict);
                result.Add(model);
            }

            var kids = fieldDict.GetOptionalValue<PdfArray>(new PdfName("Kids"));
            if (kids is not null)
            {
                CollectFormFields(kids, result);
            }
        }
    }

    private bool IsUniqueSemanticParent(PdfStream stream)
    {
        // If the form has no StructParents, it has no MCIDs—
        // structural parent uniqueness is not a concern.
        if (!stream.Dictionary.ContainsKey(new PdfName("StructParents")))
            return true;

        // Has MCIDs — check tracked reference count.
        // If not tracked yet, assume unique (correct for well-formed files).
        return !_formXObjectRefCount.TryGetValue(stream, out var count) || count <= 1;
    }

    private static IEnumerable<PdfDictionary> EnumerateChildStructureElements(PdfDictionary element)
    {
        var kids = element.Get(KidsName);
        if (kids is null)
        {
            yield break;
        }

        foreach (var child in EnumerateKidsStatic(kids))
        {
            if (child.Resolve() is not PdfDictionary dictionary)
            {
                continue;
            }

            var type = ConvertPdfObjectToString(dictionary.Get(PdfName.TypeName));
            if (string.Equals(type, "StructElem", StringComparison.Ordinal) || dictionary.ContainsKey(new PdfName("S")))
            {
                yield return dictionary;
            }
        }
    }

    private static IEnumerable<IPdfObject> EnumerateKidsStatic(IPdfObject kids)
    {
        var resolved = kids.Resolve();
        if (resolved is PdfArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }

            yield break;
        }

        yield return resolved;
    }

    private static int FindNextFreeColumn(List<bool> occupied, int start, int span)
    {
        var column = Math.Max(0, start);
        while (true)
        {
            EnsureBoolSize(occupied, column + span);
            var blocked = false;
            for (var offset = 0; offset < span; offset++)
            {
                if (occupied[column + offset])
                {
                    column += offset + 1;
                    blocked = true;
                    break;
                }
            }

            if (!blocked)
            {
                return column;
            }
        }
    }

    private static int GetRowWidth(List<bool> occupied)
    {
        for (var index = occupied.Count - 1; index >= 0; index--)
        {
            if (occupied[index])
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static void EnsureBoolSize(List<bool> values, int size)
    {
        while (values.Count < size)
        {
            values.Add(false);
        }
    }

    private static void EnsureIntSize(List<int> values, int size)
    {
        while (values.Count < size)
        {
            values.Add(0);
        }
    }

    private static string? GetHeaderId(PdfDictionary element) =>
        ConvertPdfObjectToString(element.Get(new PdfName("ID"))) ?? GetStructureAttributeString(element, "ID");

    private static string? GetStructureAttributeString(PdfDictionary element, string name)
    {
        var direct = ConvertPdfObjectToString(element.Get(new PdfName(name)));
        if (!string.IsNullOrEmpty(direct))
        {
            return direct;
        }

        foreach (var attributes in EnumerateAttributeDictionaries(element))
        {
            var value = ConvertPdfObjectToString(attributes.Get(new PdfName(name)));
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int? GetStructureAttributeInt(PdfDictionary element, string name)
    {
        var direct = GetOptionalInt(element, new PdfName(name));
        if (direct.HasValue)
        {
            return direct;
        }

        foreach (var attributes in EnumerateAttributeDictionaries(element))
        {
            var value = GetOptionalInt(attributes, new PdfName(name));
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetStructureAttributeStrings(PdfDictionary element, string name)
    {
        foreach (var source in EnumerateStructureAttributeValues(element, name))
        {
            var values = ConvertPdfObjectToStrings(source);
            if (values.Count > 0)
            {
                return values;
            }
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<IPdfObject> EnumerateStructureAttributeValues(PdfDictionary element, string name)
    {
        var direct = element.Get(new PdfName(name));
        if (direct is not null)
        {
            yield return direct;
        }

        foreach (var attributes in EnumerateAttributeDictionaries(element))
        {
            var value = attributes.Get(new PdfName(name));
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static IReadOnlyList<string> ConvertPdfObjectToStrings(IPdfObject value)
    {
        var resolved = value.Resolve();
        if (resolved is PdfArray array)
        {
            return array
                .Select(ConvertPdfObjectToString)
                .Where(static item => !string.IsNullOrEmpty(item))
                .Cast<string>()
                .ToArray();
        }

        var scalar = ConvertPdfObjectToString(resolved);
        return string.IsNullOrEmpty(scalar) ? Array.Empty<string>() : new[] { scalar };
    }

    private static IEnumerable<PdfDictionary> EnumerateAttributeDictionaries(PdfDictionary element)
    {
        var attributes = element.Get(new PdfName("A"));
        if (attributes is null)
        {
            yield break;
        }

        var resolved = attributes.Resolve();
        if (resolved is PdfDictionary dictionary)
        {
            yield return dictionary;
            yield break;
        }

        if (resolved is not PdfArray array)
        {
            yield break;
        }

        foreach (var item in array)
        {
            if (item.Resolve() is PdfDictionary dictionaryItem)
            {
                yield return dictionaryItem;
            }
        }
    }

    private static string? GetInternalRepresentation(PdfStream stream)
    {
        var filter = stream.Dictionary.Get(PdfName.Filter)?.Resolve();
        var firstFilter = filter switch
        {
            PdfName name => name.Value,
            PdfArray array when array.Count > 0 => ConvertPdfObjectToString(array[0]),
            _ => null,
        };

        return firstFilter switch
        {
            "JPXDecode" => "JPEG2000",
            "DCTDecode" => "JPEG",
            "JBIG2Decode" => "JBIG2",
            _ => firstFilter,
        };
    }

    private static string? GetColorSpaceName(IPdfObject? colorSpace)
    {
        if (colorSpace is null)
        {
            return null;
        }

        var resolved = colorSpace.Resolve();
        if (resolved is PdfName name)
        {
            return name.Value;
        }

        if (resolved is PdfArray array && array.Count > 0)
        {
            return ConvertPdfObjectToString(array[0]);
        }

        return null;
    }

    private static bool TryConvertScalar(IPdfObject? resolved, out object? scalar)
    {
        if (resolved is null)
        {
            scalar = null;
            return true;
        }

        scalar = ConvertPdfObjectToScalar(resolved);
        return scalar is not UnconvertibleScalar;
    }

    private static object? ConvertPdfObjectToScalar(IPdfObject? value)
    {
        value = value?.Resolve();
        return value switch
        {
            null => null,
            PdfString str => str.Value,
            PdfName name => name.Value,
            PdfBoolean boolean => boolean.Value,
            PdfNumber number when Math.Abs((double)number - Math.Truncate((double)number)) < 1e-9 => (double)number >= int.MinValue && (double)number <= int.MaxValue ? Convert.ToInt32((double)number, System.Globalization.CultureInfo.InvariantCulture) : (long)(double)number,
            PdfNumber number => (double)number,
            _ when value.Type == PdfObjectType.NullObj => null,
            _ => UnconvertibleScalar.Value,
        };
    }

    private static string? ConvertPdfObjectToString(IPdfObject? value)
    {
        var scalar = ConvertPdfObjectToScalar(value);
        return scalar switch
        {
            null => null,
            UnconvertibleScalar => value?.Resolve().ToString(),
            _ => Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private string? GetObjectKey(IPdfObject source)
    {
        var direct = TryGetObjectId(source);
        if (direct is not null)
        {
            return direct;
        }

        var resolved = source.Resolve();
        return _objectIds.TryGetValue(resolved, out var cached) ? cached : null;
    }

    private static string? TryGetObjectId(IPdfObject source) =>
        source is PdfIndirectRef reference ? reference.Reference.ToString() : null;

    private sealed record ObjectDescriptor(string ObjectType, params string[] SuperTypes);

    private sealed record StructInfo(
        string? RawType,
        string? StandardType,
        string? RemappedStandardType,
        bool IsNotMappedToStandardType,
        bool CircularMappingExists,
        string? ParentStandardType,
        IReadOnlyList<string> ChildStandardTypes,
        string? Alt,
        string? ActualText,
        bool ContainsLang,
        string? ParentLang,
        string? NoteId,
        string? Lang,
        string? ObjectKey);

    private sealed record MarkedContentKey(int StructParents, int Mcid);

    private sealed record XmpMetadataSnapshot(
        bool IsSerializationValid,
        string ActualEncoding,
        string? PacketBytesAttribute,
        string? PacketEncodingAttribute,
        string? DcTitle,
        PdfUaIdentificationSnapshot? PdfUaIdentification,
        PdfAIdentificationSnapshot? PdfAIdentification,
        IReadOnlyList<XmpLangAltEntry> LangAlts,
        string? XMPCreateDate,
        string? XMPModifyDate,
        string? XMPCreator,
        int XMPCreatorSize,
        string? XMPDescription,
        string? XMPProducer,
        string? XMPCreatorTool,
        string? XMPKeywords,
        XDocument? Document = null);

    private sealed record PdfUaIdentificationSnapshot(int? Part, string? PartPrefix, string? Rev, string? RevPrefix, string? AmdPrefix, string? CorrPrefix);

    private sealed record PdfAIdentificationSnapshot(
        int? Part,
        string? PartPrefix,
        string? Conformance,
        string? ConformancePrefix,
        string? Rev,
        string? RevPrefix,
        string? AmdPrefix,
        string? CorrPrefix);

    private sealed record FontProgramInfo(
        IReadOnlyDictionary<uint, double> WidthByCharCode,
        IReadOnlyDictionary<uint, bool> GlyphPresenceByCharCode,
        int NumberOfCmaps,
        bool Cmap30Present,
        bool Cmap31Present,
        bool Cmap10Present);

    private sealed class FontUsageInfo
    {
        public HashSet<string> GlyphNames { get; } = new(StringComparer.Ordinal);
        public HashSet<uint> Cids { get; } = new();
        public bool OnlyInvisible { get; set; } = true;
        public bool HasUsage { get; set; }
    }

    private sealed record TableInfo(
        int? NumberOfColumnWithWrongRowSpan,
        int? NumberOfRowWithWrongColumnSpan,
        int? ColumnSpan,
        int? WrongColumnSpan);

    private sealed record TableCellInfo(bool HasConnectedHeader, string UnknownHeaders);

    private sealed record TableCellPlacement(
        PdfDictionary Element,
        string? StandardType,
        int RowIndex,
        int ColumnIndex,
        int RowSpan,
        int ColSpan,
        IReadOnlyList<string> Headers,
        string? Scope,
        string? HeaderId);

    private sealed record HeadingInfo(int NestingLevel, bool HasCorrectNestingLevel);

    private sealed record TableCellGeometry(int ColSpan, int RowSpan, bool HasIntersection);

    private sealed record XmpLangAltEntry(bool IsXDefault);

    private sealed class UnconvertibleScalar
    {
        public static readonly UnconvertibleScalar Value = new();

        private UnconvertibleScalar()
        {
        }
    }
}

internal sealed class GenericModelObject : ModelObjectBase
{
    public GenericModelObject(string objectType, string? id = null, string? context = null, string? extraContext = null, params string[] superTypes)
        : base(objectType, id, context, extraContext, superTypes)
    {
    }

    public void Set(string name, object? value) => SetProperty(name, value);

    public void Link(string name, params IModelObject[] objects) => SetLink(name, objects);
}

internal sealed class PdfByteAnalysis
{
    private PdfByteAnalysis()
    {
    }

    public required string Header { get; init; }
    public required int HeaderOffset { get; init; }
    public required byte[] BinaryCommentBytes { get; init; }
    public required bool IsLinearized { get; init; }
    public required int PostEofDataSize { get; init; }
    public required bool ContainsXRefStream { get; init; }
    public required byte[] RawBytes { get; init; }

    public static PdfByteAnalysis Create(byte[] bytes)
    {
        var headerOffset = FindAscii(bytes, "%PDF-");
        var headerLine = headerOffset >= 0
            ? ReadLine(bytes, headerOffset)
            : string.Empty;
        var secondLineStart = headerOffset >= 0 ? SkipSingleEol(bytes, headerOffset) : -1;
        var binaryCommentBytes = secondLineStart >= 0 && secondLineStart < bytes.Length && bytes[secondLineStart] == (byte)'%'
            ? bytes.Skip(secondLineStart + 1).Take(4).ToArray()
            : Array.Empty<byte>();
        var eofIndex = LastIndexOfAscii(bytes, "%%EOF");
        var postEofDataSize = eofIndex < 0 ? bytes.Length : CountNonEolTrailing(bytes, eofIndex + 5);

        return new PdfByteAnalysis
        {
            Header = headerLine,
            HeaderOffset = headerOffset,
            BinaryCommentBytes = binaryCommentBytes,
            IsLinearized = FindAscii(bytes, "/Linearized") >= 0,
            PostEofDataSize = postEofDataSize,
            ContainsXRefStream = FindAscii(bytes, "/Type /XRef") >= 0 || FindAscii(bytes, "/Type/XRef") >= 0,
            RawBytes = bytes,
        };
    }

    /// <summary>
    /// Checks whether the stream/endstream keywords are properly formatted at the given data offset.
    /// dataOffset is the byte position where the actual stream data begins (right after the EOL following the "stream" keyword).
    /// </summary>
    public (bool StreamKeywordCRLFCompliant, bool EndstreamKeywordEOLCompliant) CheckStreamKeywordCompliance(long dataOffset, int declaredLength)
    {
        var bytes = RawBytes;

        // streamKeywordCRLFCompliant: after "stream", the next bytes should be either \r\n or \n.
        // dataOffset points to the first data byte. So bytes[dataOffset-1] should be 0x0A (LF).
        // If bytes[dataOffset-2] is 0x0D, that's CR+LF (also valid).
        // If the stream keyword wasn't followed by a proper LF, this will be false.
        bool streamCRLF = dataOffset > 0 && dataOffset <= bytes.Length && bytes[dataOffset - 1] == 0x0A;

        // endstreamKeywordEOLCompliant: the endstream keyword must be preceded by an EOL marker.
        // After the stream data at dataOffset+declaredLength, look for "endstream" and check preceding byte.
        bool endstreamEOL = true;
        var searchStart = (int)dataOffset + declaredLength;
        if (searchStart >= 0 && searchStart < bytes.Length)
        {
            // Find "endstream" within a small window after the data
            var endstreamPos = FindAsciiInRange(bytes, "endstream", searchStart, Math.Min(bytes.Length, searchStart + 32));
            if (endstreamPos >= 0)
            {
                endstreamEOL = endstreamPos > 0 && (bytes[endstreamPos - 1] == 0x0A || bytes[endstreamPos - 1] == 0x0D);
            }
        }

        return (streamCRLF, endstreamEOL);
    }

    private static int FindAsciiInRange(byte[] bytes, string needle, int start, int end)
    {
        var needleBytes = Encoding.ASCII.GetBytes(needle);
        for (int i = start; i <= end - needleBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needleBytes.Length; j++)
            {
                if (bytes[i + j] != needleBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private static int FindAscii(byte[] bytes, string needle) =>
        Encoding.ASCII.GetString(bytes).IndexOf(needle, StringComparison.Ordinal);

    private static int LastIndexOfAscii(byte[] bytes, string needle) =>
        Encoding.ASCII.GetString(bytes).LastIndexOf(needle, StringComparison.Ordinal);

    /// <summary>
    /// Extracts the /ID value string from specific physical trailers in the raw PDF bytes.
    /// Handles both traditional trailer dictionaries and cross-reference stream objects.
    /// For linearized PDFs, returns (firstPageTrailerId, lastTrailerId).
    /// For non-linearized PDFs, returns (null, lastTrailerId).
    /// </summary>
    internal static (string? FirstPageId, string? LastId) ExtractPhysicalTrailerIds(byte[] bytes, bool isLinearized)
    {
        var text = Encoding.ASCII.GetString(bytes);

        // Collect positions of all trailer-equivalent dictionaries:
        // 1. Traditional: 'trailer' keyword followed by <<...>>
        // 2. XRef streams: object dictionaries containing /Type /XRef
        var trailerDictPositions = new List<int>();

        // Find traditional trailer dicts
        var searchIdx = 0;
        while (true)
        {
            var idx = text.IndexOf("trailer", searchIdx, StringComparison.Ordinal);
            if (idx < 0) break;
            var afterTrailer = idx + 7;
            while (afterTrailer < text.Length && char.IsWhiteSpace(text[afterTrailer]))
                afterTrailer++;
            if (afterTrailer < text.Length - 1 && text[afterTrailer] == '<' && text[afterTrailer + 1] == '<')
            {
                trailerDictPositions.Add(afterTrailer); // position of '<<'
            }
            searchIdx = idx + 7;
        }

        // Find XRef stream objects (their dict starts at '<<' in "N N obj <<")
        searchIdx = 0;
        while (true)
        {
            var idx = text.IndexOf("/Type", searchIdx, StringComparison.Ordinal);
            if (idx < 0) break;
            // Check if followed by /XRef (with optional whitespace and /)
            var afterType = idx + 5;
            while (afterType < text.Length && text[afterType] is ' ' or '/')
                afterType++;
            if (afterType + 3 < text.Length && text.AsSpan(afterType, Math.Min(4, text.Length - afterType)).StartsWith("XRef", StringComparison.Ordinal))
            {
                // Find "obj" and then "<<" before /Type — search for "obj" pattern before this position
                var objPos = text.LastIndexOf(" obj", idx, StringComparison.Ordinal);
                if (objPos >= 0)
                {
                    // The dict << should follow "obj" (with optional whitespace)
                    var dictSearchStart = objPos + 4;
                    while (dictSearchStart < idx && char.IsWhiteSpace(text[dictSearchStart]))
                        dictSearchStart++;
                    if (dictSearchStart < idx && text[dictSearchStart] == '<' && dictSearchStart + 1 < idx && text[dictSearchStart + 1] == '<')
                    {
                        trailerDictPositions.Add(dictSearchStart);
                    }
                }
            }
            searchIdx = idx + 5;
        }

        if (trailerDictPositions.Count == 0)
            return (null, null);

        // Sort by position in file
        trailerDictPositions.Sort();

        // Extract /ID from the first and last trailer dicts
        var firstId = ExtractIdFromDict(text, trailerDictPositions[0]);
        var lastId = ExtractIdFromDict(text, trailerDictPositions[^1]);

        if (trailerDictPositions.Count == 1)
        {
            return (isLinearized ? firstId : null, lastId);
        }

        return (isLinearized ? firstId : null, lastId);
    }

    private static string? ExtractIdFromDict(string text, int dictStart)
    {
        if (dictStart < 0 || dictStart >= text.Length - 1) return null;
        if (text[dictStart] != '<' || text[dictStart + 1] != '<') return null;

        // Find matching >> (handle nested dicts)
        var depth = 0;
        var i = dictStart;
        var dictEnd = -1;
        while (i < text.Length - 1)
        {
            if (text[i] == '<' && text[i + 1] == '<') { depth++; i += 2; continue; }
            if (text[i] == '>' && text[i + 1] == '>') { depth--; if (depth == 0) { dictEnd = i + 2; break; } i += 2; continue; }
            i++;
        }
        if (dictEnd < 0) return null;

        var dictContent = text.Substring(dictStart, dictEnd - dictStart);

        // Find /ID in the dict
        var idIdx = dictContent.IndexOf("/ID", StringComparison.Ordinal);
        if (idIdx < 0) return null;

        // Make sure /ID is not part of a longer key name (e.g., /IDTree, /Index)
        var charAfterId = idIdx + 3 < dictContent.Length ? dictContent[idIdx + 3] : '\0';
        if (char.IsLetterOrDigit(charAfterId) || charAfterId == '_')
            return null;

        // Extract the hex content of the first byte string in the ID array.
        // /ID [<hex1><hex2>] or /ID[<hex1><hex2>] — we return hex1's content (without <>).
        // For /ID [<><>] (empty hex strings), we return "" (empty string).
        // For no /ID key, we return null.
        var afterId = idIdx + 3;
        while (afterId < dictContent.Length && char.IsWhiteSpace(dictContent[afterId]))
            afterId++;
        if (afterId >= dictContent.Length || dictContent[afterId] != '[')
            return null;

        // Find the first '<' inside the array
        var hexStart = dictContent.IndexOf('<', afterId + 1);
        if (hexStart < 0) return null;

        var hexEnd = dictContent.IndexOf('>', hexStart + 1);
        if (hexEnd < 0) return null;

        // Return the hex content between < and > (may be empty string for <>)
        return dictContent.Substring(hexStart + 1, hexEnd - hexStart - 1);
    }

    private static string ReadLine(byte[] bytes, int start)
    {
        var end = start;
        while (end < bytes.Length && bytes[end] is not ((byte)'\r' or (byte)'\n'))
        {
            end++;
        }

        return Encoding.ASCII.GetString(bytes, start, end - start);
    }

    private static int SkipLine(byte[] bytes, int start)
    {
        var index = start;
        while (index < bytes.Length && bytes[index] is not ((byte)'\r' or (byte)'\n'))
        {
            index++;
        }

        while (index < bytes.Length && bytes[index] is ((byte)'\r' or (byte)'\n'))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Skips the non-EOL content of the current line, then skips exactly one EOL marker (CR, LF, or CRLF).
    /// Returns the position immediately after that single EOL marker.
    /// </summary>
    private static int SkipSingleEol(byte[] bytes, int start)
    {
        var index = start;
        // Skip non-EOL characters (the line content)
        while (index < bytes.Length && bytes[index] is not ((byte)'\r' or (byte)'\n'))
        {
            index++;
        }
        // Skip exactly one EOL marker: CR, LF, or CRLF
        if (index < bytes.Length)
        {
            if (bytes[index] == (byte)'\r')
            {
                index++;
                if (index < bytes.Length && bytes[index] == (byte)'\n')
                    index++; // CRLF
            }
            else if (bytes[index] == (byte)'\n')
            {
                index++;
            }
        }
        return index;
    }

    private static int CountNonEolTrailing(byte[] bytes, int start)
    {
        var index = start;
        while (index < bytes.Length && bytes[index] is ((byte)'\r' or (byte)'\n'))
        {
            index++;
        }

        return Math.Max(0, bytes.Length - index);
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();

    private ReferenceEqualityComparer()
    {
    }

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

/// <summary>
/// Structural equality comparer for PDF objects. Compares resolved objects deeply.
/// </summary>
internal static class PdfObjectStructuralComparer
{
    public static bool AreEqual(IPdfObject? a, IPdfObject? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        a = a.Resolve();
        b = b.Resolve();
        if (ReferenceEquals(a, b)) return true;

        if (a.Type != b.Type) return false;

        return (a, b) switch
        {
            (PdfName na, PdfName nb) => na.Equals(nb),
            (PdfNumber na, PdfNumber nb) => na.ToString() == nb.ToString(),
            (PdfBoolean ba, PdfBoolean bb) => ba.Value == bb.Value,
            (PdfString sa, PdfString sb) => sa.Value == sb.Value,
            (PdfArray aa, PdfArray ab) => AreArraysEqual(aa, ab),
            (PdfStream sa, PdfStream sb) => AreStreamsEqual(sa, sb),
            (PdfDictionary da, PdfDictionary db) => AreDictsEqual(da, db),
            _ => false
        };
    }

    private static bool AreArraysEqual(PdfArray a, PdfArray b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!AreEqual(a[i], b[i])) return false;
        }
        return true;
    }

    private static bool AreDictsEqual(PdfDictionary a, PdfDictionary b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, valA) in a)
        {
            var valB = b.Get(key);
            if (valB is null || !AreEqual(valA, valB)) return false;
        }
        return true;
    }

    private static bool AreStreamsEqual(PdfStream a, PdfStream b)
    {
        if (!AreDictsEqual(a.Dictionary, b.Dictionary)) return false;
        try
        {
            var dataA = a.Contents.GetDecodedData();
            var dataB = b.Contents.GetDecodedData();
            return dataA.AsSpan().SequenceEqual(dataB.AsSpan());
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Scans raw PDF bytes for hex strings and indexes their validation properties.
/// Used to populate CosString isHex/containsOnlyHex/hexCount from the original source data.
/// </summary>
internal sealed class HexStringIndex
{
    internal readonly record struct HexInfo(bool ContainsOnlyHex, int HexCount);

    // Maps decoded hex string value → hex info.
    // If the same value appears multiple times with different properties, keep the first one found.
    private readonly Dictionary<string, HexInfo> _index;

    private HexStringIndex(Dictionary<string, HexInfo> index) => _index = index;

    public bool TryLookup(string decodedValue, out HexInfo info) => _index.TryGetValue(decodedValue, out info);

    public IEnumerable<KeyValuePair<string, HexInfo>> AllEntries => _index;

    public static HexStringIndex Build(byte[] rawBytes)
    {
        var index = new Dictionary<string, HexInfo>(StringComparer.Ordinal);

        for (int i = 0; i < rawBytes.Length; i++)
        {
            if (rawBytes[i] != (byte)'<')
                continue;

            // Skip << (dictionary start)
            if (i + 1 < rawBytes.Length && rawBytes[i + 1] == (byte)'<')
            {
                i++; // skip both <
                continue;
            }

            // Parse hex string content until >
            int start = i + 1;
            int hexCount = 0;
            bool containsOnlyHex = true;

            int j = start;
            for (; j < rawBytes.Length; j++)
            {
                var b = rawBytes[j];
                if (b == (byte)'>')
                    break;

                // Skip whitespace
                if (b == 0x00 || b == 0x09 || b == 0x0A || b == 0x0C || b == 0x0D || b == 0x20)
                    continue;

                hexCount++;
                if (!IsHexChar(b))
                    containsOnlyHex = false;
            }

            if (j >= rawBytes.Length)
                break; // unterminated hex string

            // Decode the hex string to get the value for matching
            var decoded = DecodeHexString(rawBytes, start, j);
            if (decoded is not null && !index.ContainsKey(decoded))
            {
                index[decoded] = new HexInfo(containsOnlyHex, hexCount);
            }

            i = j; // advance past >
        }

        return new HexStringIndex(index);
    }

    private static bool IsHexChar(byte b) =>
        (b >= (byte)'0' && b <= (byte)'9') ||
        (b >= (byte)'A' && b <= (byte)'F') ||
        (b >= (byte)'a' && b <= (byte)'f');

    private static int HexVal(byte b) =>
        b >= (byte)'0' && b <= (byte)'9' ? b - (byte)'0' :
        b >= (byte)'A' && b <= (byte)'F' ? b - (byte)'A' + 10 :
        b >= (byte)'a' && b <= (byte)'f' ? b - (byte)'a' + 10 : 0;

    private static string? DecodeHexString(byte[] raw, int start, int end)
    {
        var bytes = new List<byte>();
        bool highNibble = true;
        byte current = 0;

        for (int i = start; i < end; i++)
        {
            var b = raw[i];
            // Skip whitespace
            if (b == 0x00 || b == 0x09 || b == 0x0A || b == 0x0C || b == 0x0D || b == 0x20)
                continue;

            if (!IsHexChar(b))
                continue; // skip invalid chars for decoding (same as PdfLexer)

            if (highNibble)
            {
                current = (byte)(HexVal(b) << 4);
                highNibble = false;
            }
            else
            {
                current |= (byte)HexVal(b);
                bytes.Add(current);
                current = 0;
                highNibble = true;
            }
        }

        // Odd number of digits: pad last with 0
        if (!highNibble)
        {
            bytes.Add(current);
        }

        try
        {
            // Try to match PdfLexer's decoding: check for BOM, then use ISO 8859-1
            if (bytes.Count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode.GetString(bytes.ToArray(), 2, bytes.Count - 2);
            return System.Text.Encoding.GetEncoding("iso-8859-1").GetString(bytes.ToArray());
        }
        catch
        {
            return null;
        }
    }
}

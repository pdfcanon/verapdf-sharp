using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfLexer;
using PdfLexer.Content.Model;
using PdfLexer.DOM;
using PdfLexer.Fonts;
using PdfLexer.Fonts.Files;
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
            .FirstOrDefault(x => x.Elements().Any(e => e.Name.NamespaceName == namespaceUri));

        if (schema is null)
        {
            return PDFAFlavour.NoFlavour;
        }

        XNamespace ns = namespaceUri;
        var partText = schema.Element(ns + "part")?.Value;
        if (!int.TryParse(partText, out var part))
        {
            return PDFAFlavour.NoFlavour;
        }

        var conformance = schema.Element(ns + "conformance")?.Value?.Trim().ToLowerInvariant() ?? "";
        var id = $"{part}{conformance}";
        return PDFAFlavours.ByFlavourId(id);
    }

    private static PDFAFlavour DetectPdfUAFromXmp(XDocument document)
    {
        const string namespaceUri = "http://www.aiim.org/pdfua/ns/id/";
        var schema = document.Descendants()
            .FirstOrDefault(x => x.Elements().Any(e => e.Name.NamespaceName == namespaceUri));

        if (schema is null)
        {
            return PDFAFlavour.NoFlavour;
        }

        XNamespace ns = namespaceUri;
        var partText = schema.Element(ns + "part")?.Value;
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

internal sealed class PdfModelBuilder
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
    private static readonly PdfName DisplayDocTitleName = new("DisplayDocTitle");
    private static readonly PdfName FieldsName = new("Fields");
    private static readonly PdfName HTPName = new("HTP");
    private static readonly PdfName InterpolateName = new("Interpolate");
    private static readonly PdfName KidsName = new("K");
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
    private static readonly PdfName EFName = new("EF");
    private static readonly PdfName UFName = new("UF");
    private static readonly PdfName CTName = new("CT");
    private static readonly PdfName FTName = new("FT");
    private static readonly PdfName AnnotsName = new("Annots");

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
    private readonly Dictionary<PdfDictionary, TableInfo> _tableInfoCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, TableCellInfo> _tableCellInfoCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, HeadingInfo> _headingInfoCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly Dictionary<PdfDictionary, TableCellGeometry> _tableCellGeometryCache = new(ReferenceEqualityComparer<PdfDictionary>.Instance);
    private readonly HashSet<PdfStream> _formXObjectsWithMcids = new(ReferenceEqualityComparer<PdfStream>.Instance);
    private readonly Dictionary<PdfStream, int> _formXObjectRefCount = new(ReferenceEqualityComparer<PdfStream>.Instance);
    private readonly HashSet<string> _duplicateNoteIds = new(StringComparer.Ordinal);
    private bool _structureInitialized;
    private PdfDictionary? _currentPageDict;

    public PdfModelBuilder(PdfDocument document, PdfByteAnalysis analysis, string? sourceName)
    {
        _document = document;
        _analysis = analysis;
        _sourceName = sourceName;
    }

    public IModelObject BuildRoot()
    {
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
        root.Set("lastID", document.GetPropertyValue("lastID"));
        root.Set("lastIDValue", document.GetPropertyValue("lastIDValue"));
        root.Set("firstPageID", document.GetPropertyValue("firstPageID"));
        root.Set("firstPageIDValue", document.GetPropertyValue("firstPageIDValue"));
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

        var main = new GenericModelObject("MainXMPPackage");
        main.Set("containsPDFUAIdentification", snapshot.PdfUaIdentification is not null);
        main.Set("containsPDFAIdentification", snapshot.PdfAIdentification is not null);
        main.Set("dc_title", snapshot.DcTitle);
        main.Link("package", BuildObject(metadataStream, relationName: PdfName.Metadata.Value, parentObjectType: "PDDocument", parentPdfObject: _document.Catalog));
        result.Add(main);

        if (snapshot.PdfUaIdentification is not null)
        {
            var pdfUa = new GenericModelObject("PDFUAIdentification");
            pdfUa.Set("part", snapshot.PdfUaIdentification.Part);
            pdfUa.Set("partPrefix", snapshot.PdfUaIdentification.PartPrefix);
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

        foreach (var langAlt in snapshot.LangAlts)
        {
            var langAltObj = new GenericModelObject("XMPLangAlt");
            langAltObj.Set("xDefault", langAlt.IsXDefault);
            result.Add(langAltObj);
        }

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

        if (string.Equals(type, "XObject", StringComparison.Ordinal) || string.Equals(subtype, "Image", StringComparison.Ordinal) || string.Equals(subtype, "Form", StringComparison.Ordinal))
        {
            if (string.Equals(subtype, "Image", StringComparison.Ordinal))
            {
                var representation = GetInternalRepresentation(stream);
                return string.Equals(representation, "JPEG2000", StringComparison.Ordinal)
                    ? new ObjectDescriptor("JPEG2000", "PDXImage", "CosStream")
                    : new ObjectDescriptor("PDXImage", "CosStream");
            }

            if (string.Equals(subtype, "Form", StringComparison.Ordinal))
            {
                return new ObjectDescriptor("PDXForm", "CosStream");
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

        if (string.Equals(type, "Annot", StringComparison.Ordinal) || (subtype is not null && AnnotationSubtypes.Contains(subtype)))
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

        if (string.Equals(type, "Action", StringComparison.Ordinal) || (dictionary.ContainsKey(PdfName.S) && (dictionary.ContainsKey(PdfName.N) || string.Equals(relationName, PdfName.A.Value, StringComparison.Ordinal))))
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
        dictionary.ContainsKey(new PdfName("CA")) ||
        dictionary.ContainsKey(new PdfName("ca")) ||
        dictionary.ContainsKey(TRName) ||
        dictionary.ContainsKey(TR2Name) ||
        dictionary.ContainsKey(HTPName);

    private void PopulateStreamObject(GenericModelObject model, PdfStream stream, ObjectDescriptor descriptor, string? relationName, IPdfObject? parentPdfObject)
    {
        PopulateDictionaryObject(model, stream.Dictionary, descriptor, relationName, parentPdfObject);

        if (string.Equals(model.ObjectType, "XMPPackage", StringComparison.Ordinal))
        {
            PopulateXmpPackage(model, stream);
            return;
        }

        if (string.Equals(model.ObjectType, "ICCOutputProfile", StringComparison.Ordinal))
        {
            PopulateIccProfile(model, stream);
            return;
        }

        if (string.Equals(model.ObjectType, "PDXImage", StringComparison.Ordinal) || string.Equals(model.ObjectType, "JPEG2000", StringComparison.Ordinal))
        {
            model.Set("containsAlternates", stream.Dictionary.ContainsKey(AlternatesName));
            model.Set("containsOPI", stream.Dictionary.ContainsKey(OPIName));
            model.Set("isMask", stream.Dictionary.GetOptionalValue<PdfBoolean>(PdfName.ImageMask)?.Value ?? false);
            model.Set("internalRepresentation", GetInternalRepresentation(stream));
            model.Set("colorSpace", GetColorSpaceName(stream.Dictionary.Get(PdfName.ColorSpace)));
            return;
        }

        if (string.Equals(model.ObjectType, "PDXForm", StringComparison.Ordinal))
        {
            model.Set("containsOPI", stream.Dictionary.ContainsKey(OPIName));
            model.Set("containsPS", stream.Dictionary.ContainsKey(PSName));
            model.Set("containsRef", stream.Dictionary.ContainsKey(RefName));
            model.Set("isUniqueSemanticParent", IsUniqueSemanticParent(stream));
        }
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
        model.Set("value", ConvertPdfObjectToScalar(resolved));
    }

    private void PopulateGenericDictionary(GenericModelObject model, PdfDictionary dictionary)
    {
        foreach (var (key, value) in dictionary)
        {
            var keyName = key.Value;
            var resolved = value.Resolve();

            if (TryConvertScalar(resolved, out var scalar))
            {
                model.Set(keyName, scalar);
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
                model.Link(keyName, BuildObject(value, relationName: keyName, parentObjectType: model.ObjectType, parentPdfObject: dictionary));
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

        var markInfo = catalog.GetOptionalValue<PdfDictionary>(new PdfName("MarkInfo"));
        model.Set("MarkInfo", markInfo is not null);
        model.Set("Marked", markInfo?.GetOptionalValue<PdfBoolean>(MarkedName)?.Value ?? false);
        model.Set("Suspects", markInfo?.GetOptionalValue<PdfBoolean>(SuspectsName)?.Value ?? false);
        model.Set("NeedsRendering", catalog.GetOptionalValue<PdfBoolean>(NeedsRenderingName)?.Value ?? false);

        var viewerPreferences = catalog.GetOptionalValue<PdfDictionary>(ViewerPreferencesName);
        model.Set("DisplayDocTitle", viewerPreferences?.GetOptionalValue<PdfBoolean>(DisplayDocTitleName)?.Value ?? false);
        model.Set("ViewerPreferences", viewerPreferences is not null ? viewerPreferences.ToString() : null);

        var idArray = _document.Trailer.GetOptionalValue<PdfArray>(PdfName.ID);
        string? firstId = null;
        string? lastId = null;
        if (idArray is not null && idArray.Count > 0)
        {
            firstId = ConvertPdfObjectToString(idArray[0]);
            lastId = ConvertPdfObjectToString(idArray[Math.Min(1, idArray.Count - 1)]);
        }

        model.Set("firstPageID", firstId is not null);
        model.Set("firstPageIDValue", firstId);
        model.Set("lastID", lastId is not null);
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
    }

    private static bool ContainsMetadataStream(PdfStream? metadata) =>
        metadata is not null &&
        string.Equals(ConvertPdfObjectToString(metadata.Dictionary.Get(PdfName.TypeName)), "Metadata", StringComparison.Ordinal) &&
        string.Equals(ConvertPdfObjectToString(metadata.Dictionary.Get(PdfName.Subtype)), "XML", StringComparison.Ordinal);

    private void PopulatePage(GenericModelObject model, PdfDictionary page)
    {
        model.Set("containsAA", page.ContainsKey(AAName));
        model.Set("containsMetadata", ContainsMetadataStream(page.GetOptionalValue<PdfStream>(PdfName.Metadata)));
        model.Set("containsStructParents", page.ContainsKey(new PdfName("StructParents")));
        model.Set("Tabs", ConvertPdfObjectToString(page.Get(TabsName)));

        var annots = page.GetOptionalValue<PdfArray>(AnnotsName);
        model.Set("containsAnnotations", annots is not null && annots.Count > 0);

        var contentItems = CreatePageContentObjects(page);
        if (contentItems.Count > 0)
        {
            model.Link("contentItems", contentItems.ToArray());
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

    private List<IModelObject> CreateContentObjects(
        IReadOnlyList<IContentGroup<double>> nodes,
        PdfDictionary page,
        int? structParents,
        IReadOnlyList<string> ancestorTags,
        StructInfo? currentStructInfo,
        string? currentEffectiveLang)
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
                model.Set("ActualText", ConvertPdfObjectToString(propertyList?.Get(ActualTextName)) ?? effectiveStructInfo?.ActualText);
                model.Set("Alt", ConvertPdfObjectToString(propertyList?.Get(AltName)) ?? effectiveStructInfo?.Alt);
                model.Set("E", ConvertPdfObjectToString(propertyList?.Get(new PdfName("E"))));
                model.Set("Lang", ownLang);
                model.Set("inheritedLang", ownLang is null ? (currentEffectiveLang ?? effectiveStructInfo?.Lang ?? effectiveStructInfo?.ParentLang) : null);

                var children = CreateContentObjects(
                    mcg.Children.OfType<IContentGroup<double>>().ToList(),
                    page,
                    structParents,
                    newTags,
                    effectiveStructInfo,
                    childEffectiveLang);
                if (children.Count > 0)
                {
                    model.Link("children", children.ToArray());
                }

                result.Add(model);
                continue;
            }

            if (node is FormContent<double> form)
            {
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

            result.Add(CreateSimpleContentObject(node, ancestorTags, currentStructInfo, currentEffectiveLang));
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
        string? currentEffectiveLang = null)
    {
        var model = new GenericModelObject("SESimpleContentItem");
        model.Set("isTaggedContent", currentStructInfo is not null);
        model.Set("parentsTags", JoinTags(ancestorTags));
        model.Set("contentType", node.Type.ToString());

        if (node is TextContent<double> text)
        {
            // Create SETextItem for language tracking (rule 7.2/34)
            var textItem = new GenericModelObject("SETextItem");
            textItem.Set("Lang", currentEffectiveLang ?? currentStructInfo?.Lang ?? currentStructInfo?.ParentLang);
            model.Link("textItem", textItem);

            var glyphs = CreateGlyphObjects(text);
            if (glyphs.Count > 0)
            {
                model.Link("glyphs", glyphs.ToArray());
            }
        }

        return model;
    }

    private List<IModelObject> CreateGlyphObjects(TextContent<double> text)
    {
        var glyphs = new List<IModelObject>();
        foreach (var segment in text.Segments)
        {
            var font = segment.GraphicsState.FontObject?.Resolve() as PdfDictionary;
            var validationFont = font is null ? null : GetValidationFontDictionary(font);
            foreach (var glyphOrShift in segment.Glyphs)
            {
                if (glyphOrShift.Glyph is not { } glyph)
                {
                    continue;
                }

                if (validationFont is not null)
                {
                    RegisterFontUsage(validationFont, glyph, segment.GraphicsState.TextMode);
                    LinkTrueTypeFontProgram(validationFont);
                }

                var model = new GenericModelObject("Glyph");
                model.Set("name", glyph.Name ?? (glyph.Undefined ? ".notdef" : null));
                model.Set("renderingMode", segment.GraphicsState.TextMode);
                model.Set("isGlyphPresent", GetGlyphPresence(font, glyph));
                model.Set("widthFromDictionary", glyph.w0 == 0 && glyph.Undefined ? null : glyph.w0 * 1000d);
                model.Set("widthFromFontProgram", GetWidthFromFontProgram(font, glyph));
                model.Set("toUnicode", GetGlyphUnicode(glyph));
                glyphs.Add(model);
            }
        }

        return glyphs;
    }

    private void PopulateAnnotation(GenericModelObject model, PdfDictionary annotation, IPdfObject? parentPdfObject)
    {
        model.Set("containsAA", annotation.ContainsKey(AAName));
        model.Set("isOutsideCropBox", IsOutsideCropBox(annotation, parentPdfObject));
        model.Set("Contents", ConvertPdfObjectToString(annotation.Get(PdfName.Contents)));

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
        model.Set("S", ConvertPdfObjectToString(outputIntent.Get(PdfName.S)));
    }

    private static void PopulateExtGState(GenericModelObject model, PdfDictionary extGState)
    {
        model.Set("containsTR", extGState.ContainsKey(TRName));
        model.Set("containsTR2", extGState.ContainsKey(TR2Name));
        model.Set("containsHTP", extGState.ContainsKey(HTPName));
        model.Set("TR2NameValue", ConvertPdfObjectToString(extGState.Get(TR2Name)));
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
                // veraPDF wouldn't create a validation object for unused fonts.
                // Retype to a non-validated type so specific-type rules don't match,
                // and set renderingMode=3 so supertype-matched rules (PDFont) also pass.
                fontObj.ObjectType = "PDUnusedFont";
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
        model.Set("CIDToGIDMap", ConvertPdfObjectToString((descendantFont ?? font).Get(PdfName.CIDToGIDMap)));

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

        LinkTrueTypeFontProgram(font);
        UpdateFontCoverageProperties(font);
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
                cmaps.Any(static x => x.PlatformId == 3 && x.EncodingId == 0));
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

    private double? GetWidthFromFontProgram(PdfDictionary? font, Glyph glyph)
    {
        if (font is null || !glyph.CodePoint.HasValue)
        {
            return null;
        }

        var validationFont = GetValidationFontDictionary(font);
        var program = GetTrueTypeFontProgramInfo(validationFont);
        return program is not null && program.WidthByCharCode.TryGetValue(glyph.CodePoint.Value, out var width)
            ? width
            : null;
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

    private static void PopulateFileSpecification(GenericModelObject model, PdfDictionary fileSpec)
    {
        model.Set("F", ConvertPdfObjectToString(fileSpec.Get(PdfName.F)));
        model.Set("UF", ConvertPdfObjectToString(fileSpec.Get(UFName)));
        model.Set("containsEF", fileSpec.ContainsKey(EFName));
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
            var text = actualEncoding.GetString(bytes);
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
            var pdfUa = document is null ? null : ReadPdfUaIdentification(document);
            var pdfa = document is null ? null : ReadPdfAIdentification(document);
            var langAlts = document is null ? Array.Empty<XmpLangAltEntry>() : ReadXmpLangAlts(document);
            snapshot = new XmpMetadataSnapshot(valid, actualEncoding.WebName.ToUpperInvariant(), packetBytes, packetEncoding, dcTitle, pdfUa, pdfa, langAlts);
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
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

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

    private static PdfUaIdentificationSnapshot? ReadPdfUaIdentification(XDocument document)
    {
        const string namespaceUri = "http://www.aiim.org/pdfua/ns/id/";
        var schema = FindSchemaNode(document, namespaceUri);
        if (schema is null)
        {
            return null;
        }

        XNamespace ns = namespaceUri;

        return new PdfUaIdentificationSnapshot(
            ParseInt(schema.Element(ns + "part")?.Value),
            GetPrefix(schema.Element(ns + "part")),
            GetPrefix(schema.Element(ns + "amd")),
            GetPrefix(schema.Element(ns + "corr")));
    }

    private static PdfAIdentificationSnapshot? ReadPdfAIdentification(XDocument document)
    {
        const string namespaceUri = "http://www.aiim.org/pdfa/ns/id/";
        var schema = FindSchemaNode(document, namespaceUri);
        if (schema is null)
        {
            return null;
        }

        XNamespace ns = namespaceUri;

        return new PdfAIdentificationSnapshot(
            ParseInt(schema.Element(ns + "part")?.Value),
            GetPrefix(schema.Element(ns + "part")),
            schema.Element(ns + "conformance")?.Value,
            GetPrefix(schema.Element(ns + "conformance")),
            schema.Element(ns + "rev")?.Value,
            GetPrefix(schema.Element(ns + "rev")),
            GetPrefix(schema.Element(ns + "amd")),
            GetPrefix(schema.Element(ns + "corr")));
    }

    private static XElement? FindSchemaNode(XDocument document, string namespaceUri) =>
        document.Descendants()
            .FirstOrDefault(x => x.Elements().Any(e => e.Name.NamespaceName == namespaceUri));

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
        var standardType = ResolveStandardType(rawType, out var circular, out var remappedStandardType);
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

    private string? ResolveStandardType(string? rawType, out bool circular, out string? remappedStandardType)
    {
        circular = false;
        remappedStandardType = null;
        if (string.IsNullOrEmpty(rawType))
        {
            return null;
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
            return ResolveStandardType(rawType, out _, out _);
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

        var defaultConfig = ocProperties.GetOptionalValue<PdfDictionary>(DName);
        if (defaultConfig is not null)
        {
            var model = new GenericModelObject("PDOCConfig");
            PopulateOcConfig(model, defaultConfig);
            result.Add(model);
        }

        var configs = ocProperties.GetOptionalValue<PdfArray>(ConfigsName);
        if (configs is not null)
        {
            foreach (var item in configs)
            {
                if (item.Resolve() is PdfDictionary configDict)
                {
                    var model = new GenericModelObject("PDOCConfig");
                    PopulateOcConfig(model, configDict);
                    result.Add(model);
                }
            }
        }

        return result;
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
            PdfNumber number when Math.Abs((double)number - Math.Truncate((double)number)) < 1e-9 => Convert.ToInt32((double)number, System.Globalization.CultureInfo.InvariantCulture),
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
        IReadOnlyList<XmpLangAltEntry> LangAlts);

    private sealed record PdfUaIdentificationSnapshot(int? Part, string? PartPrefix, string? AmdPrefix, string? CorrPrefix);

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
        bool Cmap30Present);

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

    public static PdfByteAnalysis Create(byte[] bytes)
    {
        var headerOffset = FindAscii(bytes, "%PDF-");
        var headerLine = headerOffset >= 0
            ? ReadLine(bytes, headerOffset)
            : string.Empty;
        var secondLineStart = headerOffset >= 0 ? SkipLine(bytes, headerOffset) : -1;
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
        };
    }

    private static int FindAscii(byte[] bytes, string needle) =>
        Encoding.ASCII.GetString(bytes).IndexOf(needle, StringComparison.Ordinal);

    private static int LastIndexOfAscii(byte[] bytes, string needle) =>
        Encoding.ASCII.GetString(bytes).LastIndexOf(needle, StringComparison.Ordinal);

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

using System.Text;
using VeraPdfSharp.Core;
using VeraPdfSharp.Model;

namespace VeraPdfSharp.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Parser_Extracts_Metadata_OutputIntent_And_AnnotationStructure()
    {
        var bytes = BuildSamplePdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFA2B, "sample.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));

        Assert.Equal("PDDocument", document.ObjectType);
        Assert.Equal(true, document.GetPropertyValue("containsMetadata"));
        Assert.Equal(true, document.GetPropertyValue("containsStructTreeRoot"));
        Assert.Equal(true, document.GetPropertyValue("Marked"));

        var mainXmp = Assert.Single(root.GetLinkedObjects("metadata"), static x => x.ObjectType == "MainXMPPackage");
        Assert.Equal("Example title", mainXmp.GetPropertyValue("dc_title"));
        Assert.Equal(true, mainXmp.GetPropertyValue("containsPDFAIdentification"));
        Assert.Equal(true, mainXmp.GetPropertyValue("containsPDFUAIdentification"));

        var outputIntent = Assert.Single(document.GetLinkedObjects("OutputIntents"));
        Assert.Equal("PDOutputIntent", outputIntent.ObjectType);
        Assert.Equal("GTS_PDFA1", outputIntent.GetPropertyValue("S"));

        var iccProfile = Assert.Single(outputIntent.GetLinkedObjects("DestOutputProfile"));
        Assert.Equal("ICCOutputProfile", iccProfile.ObjectType);
        Assert.Equal("prtr", iccProfile.GetPropertyValue("deviceClass"));
        Assert.Equal("RGB ", iccProfile.GetPropertyValue("colorSpace"));

        var pageTree = Assert.Single(document.GetLinkedObjects("Pages"));
        var page = Assert.Single(pageTree.GetLinkedObjects("Kids"));
        Assert.Equal("PDPage", page.ObjectType);

        var annotation = Assert.Single(page.GetLinkedObjects("Annots"));
        Assert.Equal("PDWidgetAnnot", annotation.ObjectType);
        Assert.Equal(true, annotation.GetPropertyValue("isOutsideCropBox"));
        Assert.Equal("Table", annotation.GetPropertyValue("structParentStandardType"));
        Assert.Equal("Alt table", annotation.GetPropertyValue("Alt"));
    }

    [Fact]
    public void Parser_Extracts_XmpPackage_Encoding_And_PdfaRevision()
    {
        var bytes = BuildSamplePdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFA4, "sample.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var xmpPackage = Assert.Single(document.GetLinkedObjects("Metadata"));

        Assert.Equal("XMPPackage", xmpPackage.ObjectType);
        Assert.Equal("UTF-8", xmpPackage.GetPropertyValue("actualEncoding"));
        Assert.Equal(true, xmpPackage.GetPropertyValue("isSerializationValid"));
        Assert.Null(xmpPackage.GetPropertyValue("bytes"));
        Assert.Null(xmpPackage.GetPropertyValue("encoding"));

        var pdfaIdentification = Assert.Single(root.GetLinkedObjects("metadata"), static x => x.ObjectType == "PDFAIdentification");
        Assert.Equal(4, pdfaIdentification.GetPropertyValue("part"));
        Assert.Equal("2020", pdfaIdentification.GetPropertyValue("rev"));
    }

    [Fact]
    public void Parser_Extracts_MarkedContent_SimpleContent_And_Glyphs()
    {
        var bytes = BuildSamplePdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "sample.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var pageTree = Assert.Single(document.GetLinkedObjects("Pages"));
        var page = Assert.Single(pageTree.GetLinkedObjects("Kids"));
        var contentItems = page.GetLinkedObjects("contentItems");

        var artifact = Assert.Single(contentItems, static x => x.ObjectType == "SEMarkedContent" && Equals(x.GetPropertyValue("tag"), "Artifact"));
        Assert.Equal(false, artifact.GetPropertyValue("isTaggedContent"));

        var artifactLeaf = Assert.Single(artifact.GetLinkedObjects("children"));
        Assert.Equal("SESimpleContentItem", artifactLeaf.ObjectType);
        Assert.Equal(new[] { "Artifact" }, artifactLeaf.GetPropertyValue("parentsTags"));

        var span = Assert.Single(contentItems, static x => x.ObjectType == "SEMarkedContent" && Equals(x.GetPropertyValue("tag"), "Span"));
        Assert.Equal(true, span.GetPropertyValue("isTaggedContent"));
        Assert.Equal("15 0 R", span.GetPropertyValue("parentStructureElementObjectKey"));
        Assert.Equal("Tagged B", span.GetPropertyValue("ActualText"));
        Assert.Equal("Alt B", span.GetPropertyValue("Alt"));
        Assert.Equal("en-CA", span.GetPropertyValue("Lang"));

        var spanLeaf = Assert.Single(span.GetLinkedObjects("children"));
        var glyph = Assert.Single(spanLeaf.GetLinkedObjects("glyphs"));
        Assert.Equal("B", glyph.GetPropertyValue("toUnicode"));
        Assert.Equal(0, glyph.GetPropertyValue("renderingMode"));
        Assert.NotNull(glyph.GetPropertyValue("widthFromDictionary"));
    }

    [Fact]
    public void Parser_Extracts_Link_List_Table_And_Note_Semantics()
    {
        var bytes = BuildAdvancedStructurePdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "advanced.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var pageTree = Assert.Single(document.GetLinkedObjects("Pages"));
        var page = Assert.Single(pageTree.GetLinkedObjects("Kids"));

        var link = Assert.Single(page.GetLinkedObjects("Annots"));
        Assert.Equal("PDLinkAnnot", link.ObjectType);
        Assert.Equal("Link", link.GetPropertyValue("structParentStandardType"));
        Assert.Equal("Example link", link.GetPropertyValue("Contents"));

        var structTreeRoot = Assert.Single(document.GetLinkedObjects("StructTreeRoot"));
        var topLevelStructs = structTreeRoot.GetLinkedObjects("K").ToArray();

        Assert.Contains(topLevelStructs, static x => x.ObjectType == "SEL");
        Assert.Contains(topLevelStructs, static x => x.ObjectType == "SELI");
        Assert.Contains(topLevelStructs, static x => x.ObjectType == "SELbl");
        Assert.Contains(topLevelStructs, static x => x.ObjectType == "SELBody");

        var mismatchedTable = Assert.Single(topLevelStructs, static x => Equals(x.GetPropertyValue("ID"), "table-mismatch"));
        Assert.Equal("SETable", mismatchedTable.ObjectType);
        Assert.Equal(1, mismatchedTable.GetPropertyValue("numberOfColumnWithWrongRowSpan"));
        Assert.Equal(1, mismatchedTable.GetPropertyValue("numberOfRowWithWrongColumnSpan"));
        Assert.Equal(2, mismatchedTable.GetPropertyValue("columnSpan"));
        Assert.Equal(1, mismatchedTable.GetPropertyValue("wrongColumnSpan"));

        var mismatchRow = Assert.Single(mismatchedTable.GetLinkedObjects("K"), static x => x.ObjectType == "SETR" && Equals(x.GetPropertyValue("ID"), "table-mismatch-row-2"));
        var connectedTd = Assert.Single(mismatchRow.GetLinkedObjects("K"));
        Assert.Equal("SETD", connectedTd.ObjectType);
        Assert.Equal(true, connectedTd.GetPropertyValue("hasConnectedHeader"));
        Assert.Equal(string.Empty, connectedTd.GetPropertyValue("unknownHeaders"));

        var headerTable = Assert.Single(topLevelStructs, static x => Equals(x.GetPropertyValue("ID"), "table-headers"));
        var headerRow = Assert.Single(headerTable.GetLinkedObjects("K"), static x => x.ObjectType == "SETR" && Equals(x.GetPropertyValue("ID"), "table-headers-row-2"));
        var unresolvedTd = Assert.Single(headerRow.GetLinkedObjects("K"));
        Assert.Equal(false, unresolvedTd.GetPropertyValue("hasConnectedHeader"));
        Assert.Equal("missing", unresolvedTd.GetPropertyValue("unknownHeaders"));

        var notes = topLevelStructs.Where(static x => x.ObjectType == "SENote").ToArray();
        Assert.Equal(2, notes.Length);
        Assert.All(notes, static note => Assert.Equal(true, note.GetPropertyValue("hasDuplicateNoteID")));
    }

    [Fact]
    public void Parser_Extracts_HeadingNesting_And_CosLang()
    {
        var bytes = BuildHeadingNestingPdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "heading.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var structTreeRoot = Assert.Single(document.GetLinkedObjects("StructTreeRoot"));
        var elements = structTreeRoot.GetLinkedObjects("K").ToArray();

        // H1 → first heading, always correct
        var h1 = Assert.Single(elements, static x => Equals(x.GetPropertyValue("valueS"), "H1"));
        Assert.Equal("SEHn", h1.ObjectType);
        Assert.Equal(1, h1.GetPropertyValue("nestingLevel"));
        Assert.Equal(true, h1.GetPropertyValue("hasCorrectNestingLevel"));

        // H2 → follows H1 without skip, correct
        var h2 = Assert.Single(elements, static x => Equals(x.GetPropertyValue("valueS"), "H2"));
        Assert.Equal(2, h2.GetPropertyValue("nestingLevel"));
        Assert.Equal(true, h2.GetPropertyValue("hasCorrectNestingLevel"));

        // H4 → skips H3, incorrect
        var h4 = Assert.Single(elements, static x => Equals(x.GetPropertyValue("valueS"), "H4"));
        Assert.Equal(4, h4.GetPropertyValue("nestingLevel"));
        Assert.Equal(false, h4.GetPropertyValue("hasCorrectNestingLevel"));

        // CosLang should be emitted for the catalog Lang entry
        var docLangObjects = document.GetLinkedObjects("Lang");
        var cosLang = Assert.Single(docLangObjects);
        Assert.Equal("CosLang", cosLang.ObjectType);
        Assert.Equal("en-US", cosLang.GetPropertyValue("unicodeValue"));

        // H1 has Lang "fr" → should have CosLang child
        var h1Langs = h1.GetLinkedObjects("Lang");
        var h1CosLang = Assert.Single(h1Langs);
        Assert.Equal("fr", h1CosLang.GetPropertyValue("unicodeValue"));
    }

    [Fact]
    public void Parser_Extracts_TOC_TOCI_And_Caption_Types()
    {
        var bytes = BuildTocStructurePdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "toc.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var structTreeRoot = Assert.Single(document.GetLinkedObjects("StructTreeRoot"));
        var elements = structTreeRoot.GetLinkedObjects("K").ToArray();

        var toc = Assert.Single(elements, static x => x.ObjectType == "SETOC");
        Assert.Equal("TOC", toc.GetPropertyValue("valueS"));
        Assert.Equal("TOC", toc.GetPropertyValue("standardType"));

        var tocKids = toc.GetLinkedObjects("K").ToArray();
        Assert.Contains(tocKids, static x => x.ObjectType == "SETOCI");
        Assert.Contains(tocKids, static x => x.ObjectType == "SECaption");

        var toci = Assert.Single(tocKids, static x => x.ObjectType == "SETOCI");
        Assert.Equal("TOC", toci.GetPropertyValue("parentStandardType"));
    }

    [Fact]
    public void Parser_Extracts_Page_Annotations_And_Tabs()
    {
        var bytes = BuildPageAnnotationsPdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "page-annots.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var pageTree = Assert.Single(document.GetLinkedObjects("Pages"));
        var page = Assert.Single(pageTree.GetLinkedObjects("Kids"));

        Assert.Equal(true, page.GetPropertyValue("containsAnnotations"));
        Assert.Equal("S", page.GetPropertyValue("Tabs"));
    }

    [Fact]
    public void Parser_Extracts_FormFields_And_OcConfig()
    {
        var bytes = BuildFormFieldAndOcPdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "form-oc.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));

        // Form fields from AcroForm
        var formFields = document.GetLinkedObjects("formFields").ToArray();
        Assert.True(formFields.Length >= 1);
        var firstField = formFields[0];
        Assert.Equal("PDFormField", firstField.ObjectType);
        Assert.Equal("Tx", firstField.GetPropertyValue("FT"));
        Assert.Equal("First name", firstField.GetPropertyValue("TU"));

        // OC Config
        var ocConfigs = document.GetLinkedObjects("ocConfigs").ToArray();
        Assert.True(ocConfigs.Length >= 1);
        var defaultConfig = ocConfigs[0];
        Assert.Equal("PDOCConfig", defaultConfig.ObjectType);
        Assert.Equal("Default", defaultConfig.GetPropertyValue("Name"));
    }

    [Fact]
    public void Parser_Extracts_XMPLangAlt_Entries()
    {
        var bytes = BuildSamplePdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "sample.pdf");
        var root = parser.GetRoot();
        var langAlts = root.GetLinkedObjects("metadata")
            .Where(static x => x.ObjectType == "XMPLangAlt")
            .ToArray();

        Assert.Single(langAlts);
        Assert.Equal(true, langAlts[0].GetPropertyValue("xDefault"));
    }

    [Fact]
    public void Parser_Extracts_SETextItem_For_Text_Content()
    {
        var bytes = BuildSamplePdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "sample.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var pageTree = Assert.Single(document.GetLinkedObjects("Pages"));
        var page = Assert.Single(pageTree.GetLinkedObjects("Kids"));
        var contentItems = page.GetLinkedObjects("contentItems");

        var span = Assert.Single(contentItems, static x => x.ObjectType == "SEMarkedContent" && Equals(x.GetPropertyValue("tag"), "Span"));
        var spanLeaf = Assert.Single(span.GetLinkedObjects("children"));

        var textItems = spanLeaf.GetLinkedObjects("textItem").ToArray();
        Assert.Single(textItems);
        Assert.Equal("SETextItem", textItems[0].ObjectType);
        Assert.Equal("en-CA", textItems[0].GetPropertyValue("Lang"));
    }

    [Fact]
    public void Parser_Extracts_TableCell_Geometry_And_SETableCell_Supertype()
    {
        var bytes = BuildTableCellGeometryPdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "tablegeo.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var structTreeRoot = Assert.Single(document.GetLinkedObjects("StructTreeRoot"));
        var table = Assert.Single(structTreeRoot.GetLinkedObjects("K"), static x => x.ObjectType == "SETable");
        var rows = table.GetLinkedObjects("K").ToArray();

        var row1Cells = rows[0].GetLinkedObjects("K").ToArray();
        var th = row1Cells[0];
        Assert.Equal("SETH", th.ObjectType);
        Assert.Contains("SETableCell", th.SuperTypes);
        Assert.Equal(2, th.GetPropertyValue("ColSpan"));
        Assert.Equal(1, th.GetPropertyValue("RowSpan"));

        var row2Cells = rows[1].GetLinkedObjects("K").ToArray();
        var td1 = row2Cells[0];
        Assert.Equal("SETD", td1.ObjectType);
        Assert.Contains("SETableCell", td1.SuperTypes);
        Assert.Equal(1, td1.GetPropertyValue("ColSpan"));
        Assert.Equal(1, td1.GetPropertyValue("RowSpan"));
        Assert.Equal(false, td1.GetPropertyValue("hasIntersection"));
    }

    [Fact]
    public void Parser_Extracts_Annotation_Subtypes_TrapNet_And_PrinterMark()
    {
        var bytes = BuildAnnotationSubtypesPdf();

        using var parser = PdfLexerValidationParser.FromBytes(bytes, PDFAFlavour.PDFUA1, "annots.pdf");
        var root = parser.GetRoot();
        var document = Assert.Single(root.GetLinkedObjects("document"));
        var pageTree = Assert.Single(document.GetLinkedObjects("Pages"));
        var page = Assert.Single(pageTree.GetLinkedObjects("Kids"));
        var annots = page.GetLinkedObjects("Annots").ToArray();

        Assert.Contains(annots, static x => x.ObjectType == "PDTrapNetAnnot");
        Assert.Contains(annots, static x => x.ObjectType == "PDPrinterMarkAnnot");
    }

    private static byte[] BuildHeadingNestingPdf()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/StructTreeRoot 10 0 R
/Lang (en-US)
/MarkInfo << /Marked true >>
>>
"""),
            [2] = Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            [3] = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
            [5] = Ascii("<< /Producer (Test) >>"),
            [10] = Ascii("<< /Type /StructTreeRoot /K [11 0 R 12 0 R 13 0 R] >>"),
            [11] = Ascii("<< /Type /StructElem /S /H1 /P 10 0 R /Lang (fr) /K [] >>"),
            [12] = Ascii("<< /Type /StructElem /S /H2 /P 10 0 R /K [] >>"),
            [13] = Ascii("<< /Type /StructElem /S /H4 /P 10 0 R /K [] >>"),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] BuildTocStructurePdf()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/StructTreeRoot 10 0 R
/MarkInfo << /Marked true >>
>>
"""),
            [2] = Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            [3] = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
            [5] = Ascii("<< /Producer (Test) >>"),
            [10] = Ascii("<< /Type /StructTreeRoot /K [11 0 R] >>"),
            [11] = Ascii("<< /Type /StructElem /S /TOC /P 10 0 R /K [12 0 R 13 0 R] >>"),
            [12] = Ascii("<< /Type /StructElem /S /Caption /P 11 0 R /K [] >>"),
            [13] = Ascii("<< /Type /StructElem /S /TOCI /P 11 0 R /K [] >>"),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] BuildPageAnnotationsPdf()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/StructTreeRoot 10 0 R
/MarkInfo << /Marked true >>
>>
"""),
            [2] = Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            [3] = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Tabs /S /Annots [4 0 R] >>"),
            [4] = Ascii("<< /Type /Annot /Subtype /Link /Rect [10 10 50 20] /Contents (Test) >>"),
            [5] = Ascii("<< /Producer (Test) >>"),
            [10] = Ascii("<< /Type /StructTreeRoot /K [] >>"),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] BuildFormFieldAndOcPdf()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/AcroForm 6 0 R
/StructTreeRoot 10 0 R
/MarkInfo << /Marked true >>
/OCProperties << /D << /Name (Default) >> >>
>>
"""),
            [2] = Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            [3] = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
            [5] = Ascii("<< /Producer (Test) >>"),
            [6] = Ascii("<< /Fields [7 0 R] >>"),
            [7] = Ascii("<< /FT /Tx /T (name) /TU (First name) >>"),
            [10] = Ascii("<< /Type /StructTreeRoot /K [] >>"),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] BuildTableCellGeometryPdf()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/StructTreeRoot 10 0 R
/MarkInfo << /Marked true >>
>>
"""),
            [2] = Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            [3] = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
            [5] = Ascii("<< /Producer (Test) >>"),
            [10] = Ascii("<< /Type /StructTreeRoot /K [11 0 R] >>"),
            [11] = Ascii("<< /Type /StructElem /S /Table /P 10 0 R /K [12 0 R 13 0 R] >>"),
            [12] = Ascii("<< /Type /StructElem /S /TR /P 11 0 R /K [14 0 R] >>"),
            [13] = Ascii("<< /Type /StructElem /S /TR /P 11 0 R /K [15 0 R 16 0 R] >>"),
            [14] = Ascii("<< /Type /StructElem /S /TH /P 12 0 R /A << /ColSpan 2 /Scope /Column >> /K [] >>"),
            [15] = Ascii("<< /Type /StructElem /S /TD /P 13 0 R /K [] >>"),
            [16] = Ascii("<< /Type /StructElem /S /TD /P 13 0 R /K [] >>"),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] BuildAnnotationSubtypesPdf()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/StructTreeRoot 10 0 R
/MarkInfo << /Marked true >>
>>
"""),
            [2] = Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            [3] = Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 6 0 R] >>"),
            [4] = Ascii("<< /Type /Annot /Subtype /TrapNet /Rect [10 10 50 20] >>"),
            [5] = Ascii("<< /Producer (Test) >>"),
            [6] = Ascii("<< /Type /Annot /Subtype /PrinterMark /Rect [60 10 90 20] >>"),
            [10] = Ascii("<< /Type /StructTreeRoot /K [] >>"),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] BuildSamplePdf()
    {
        var metadataXml = """
<?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
  <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
    <rdf:Description rdf:about=""
      xmlns:dc="http://purl.org/dc/elements/1.1/"
      xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/"
      xmlns:pdfuaid="http://www.aiim.org/pdfua/ns/id/">
      <dc:title>
        <rdf:Alt>
          <rdf:li xml:lang="x-default">Example title</rdf:li>
        </rdf:Alt>
      </dc:title>
      <pdfaid:part>4</pdfaid:part>
      <pdfaid:rev>2020</pdfaid:rev>
      <pdfaid:conformance>B</pdfaid:conformance>
      <pdfuaid:part>1</pdfuaid:part>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>
""";

        var icc = CreateIccProfile();
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/Metadata 6 0 R
/OutputIntents [7 0 R]
/AcroForm 10 0 R
/StructTreeRoot 11 0 R
/Lang (en-CA)
/MarkInfo << /Marked true >>
>>
"""),
            [2] = Ascii("""
<< /Type /Pages /Count 1 /Kids [3 0 R] >>
"""),
            [3] = Ascii("""
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /CropBox [0 0 200 200] /Resources << /Font << /F1 14 0 R >> >> /Contents 9 0 R /Annots [4 0 R] /StructParents 0 >>
"""),
            [4] = Ascii("""
<< /Type /Annot /Subtype /Widget /Rect [250 250 260 260] /StructParent 1 /F 4 /TU (Field help) >>
"""),
            [5] = Ascii("""
<< /Producer (VeraPdfSharp Tests) >>
"""),
            [6] = StreamObject("<< /Type /Metadata /Subtype /XML >>", Encoding.UTF8.GetBytes(metadataXml)),
            [7] = Ascii("""
<< /Type /OutputIntent /S /GTS_PDFA1 /DestOutputProfile 8 0 R >>
"""),
            [8] = StreamObject("<< /N 3 >>", icc),
            [9] = StreamObject("<< >>", Ascii("""
/Artifact <<>> BDC
BT
/F1 12 Tf
10 140 Td
(A) Tj
ET
EMC
/Span << /MCID 0 /ActualText (Tagged B) /Alt (Alt B) /Lang (en-CA) >> BDC
BT
/F1 12 Tf
10 120 Td
(B) Tj
ET
EMC
""")),
            [10] = Ascii("""
<< /Fields [] /NeedAppearances false >>
"""),
            [11] = Ascii("""
<< /Type /StructTreeRoot /K [12 0 R 15 0 R] /ParentTree 13 0 R /RoleMap << /CustomTable /Table >> >>
"""),
            [12] = Ascii("""
<< /Type /StructElem /S /CustomTable /P 11 0 R /Alt (Alt table) /K [<< /Type /OBJR /Obj 4 0 R /Pg 3 0 R >>] >>
"""),
            [13] = Ascii("""
<< /Nums [0 [15 0 R] 1 12 0 R] >>
"""),
            [14] = Ascii("""
<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
"""),
            [15] = Ascii("""
<< /Type /StructElem /S /Span /P 11 0 R /Pg 3 0 R /Alt (Struct Alt) /ActualText (Struct Text) /K [<< /Type /MCR /Pg 3 0 R /MCID 0 >>] >>
"""),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] BuildAdvancedStructurePdf()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("""
<<
/Type /Catalog
/Pages 2 0 R
/StructTreeRoot 11 0 R
/MarkInfo << /Marked true >>
>>
"""),
            [2] = Ascii("""
<< /Type /Pages /Count 1 /Kids [3 0 R] >>
"""),
            [3] = Ascii("""
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /CropBox [0 0 200 200] /Annots [4 0 R] /StructParents 0 >>
"""),
            [4] = Ascii("""
<< /Type /Annot /Subtype /Link /Rect [10 10 40 20] /StructParent 0 /Contents (Example link) >>
"""),
            [5] = Ascii("""
<< /Producer (VeraPdfSharp Tests) >>
"""),
            [11] = Ascii("""
<< /Type /StructTreeRoot /K [12 0 R 15 0 R 16 0 R 17 0 R 18 0 R 20 0 R 30 0 R 40 0 R 41 0 R] /ParentTree 13 0 R >>
"""),
            [12] = Ascii("""
<< /Type /StructElem /S /Link /P 11 0 R /Alt (Link alt) /K [<< /Type /OBJR /Obj 4 0 R /Pg 3 0 R >>] >>
"""),
            [13] = Ascii("""
<< /Nums [0 12 0 R] >>
"""),
            [15] = Ascii("""
<< /Type /StructElem /S /L /P 11 0 R /K [16 0 R] >>
"""),
            [16] = Ascii("""
<< /Type /StructElem /S /LI /P 15 0 R /K [17 0 R 18 0 R] >>
"""),
            [17] = Ascii("""
<< /Type /StructElem /S /Lbl /P 16 0 R /K [] >>
"""),
            [18] = Ascii("""
<< /Type /StructElem /S /LBody /P 16 0 R /K [] >>
"""),
            [20] = Ascii("""
<< /Type /StructElem /S /Table /P 11 0 R /ID (table-mismatch) /K [21 0 R 22 0 R] >>
"""),
            [21] = Ascii("""
<< /Type /StructElem /S /TR /P 20 0 R /ID (table-mismatch-row-1) /K [23 0 R 24 0 R] >>
"""),
            [22] = Ascii("""
<< /Type /StructElem /S /TR /P 20 0 R /ID (table-mismatch-row-2) /K [25 0 R] >>
"""),
            [23] = Ascii("""
<< /Type /StructElem /S /TH /P 21 0 R /ID (h1) /A << /Scope /Column >> /K [] >>
"""),
            [24] = Ascii("""
<< /Type /StructElem /S /TH /P 21 0 R /ID (h2) /A << /Scope /Column >> /K [] >>
"""),
            [25] = Ascii("""
<< /Type /StructElem /S /TD /P 22 0 R /K [] >>
"""),
            [30] = Ascii("""
<< /Type /StructElem /S /Table /P 11 0 R /ID (table-headers) /K [31 0 R 32 0 R] >>
"""),
            [31] = Ascii("""
<< /Type /StructElem /S /TR /P 30 0 R /ID (table-headers-row-1) /K [33 0 R] >>
"""),
            [32] = Ascii("""
<< /Type /StructElem /S /TR /P 30 0 R /ID (table-headers-row-2) /K [34 0 R] >>
"""),
            [33] = Ascii("""
<< /Type /StructElem /S /TH /P 31 0 R /ID (known) /A << /Scope /Column >> /K [] >>
"""),
            [34] = Ascii("""
<< /Type /StructElem /S /TD /P 32 0 R /A << /Headers [(missing)] >> /K [] >>
"""),
            [40] = Ascii("""
<< /Type /StructElem /S /Note /P 11 0 R /ID (note-1) /K [] >>
"""),
            [41] = Ascii("""
<< /Type /StructElem /S /Note /P 11 0 R /ID (note-1) /K [] >>
"""),
        };

        return BuildPdf(objects, rootObject: 1, infoObject: 5);
    }

    private static byte[] CreateIccProfile()
    {
        var bytes = new byte[128];
        bytes[8] = 4;
        bytes[9] = 0;
        Encoding.ASCII.GetBytes("prtr").CopyTo(bytes, 12);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(bytes, 16);
        return bytes;
    }

    private static byte[] BuildPdf(IReadOnlyDictionary<int, byte[]> objects, int rootObject, int infoObject)
    {
        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var offsets = new Dictionary<int, long>();

        writer.Write(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        writer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        foreach (var objectId in objects.Keys.OrderBy(static x => x))
        {
            offsets[objectId] = stream.Position;
            writer.Write(Encoding.ASCII.GetBytes($"{objectId} 0 obj\n"));
            writer.Write(objects[objectId]);
            if (objects[objectId].Length == 0 || objects[objectId][^1] != (byte)'\n')
            {
                writer.Write((byte)'\n');
            }

            writer.Write(Encoding.ASCII.GetBytes("endobj\n"));
        }

        var xrefOffset = stream.Position;
        var size = objects.Keys.Max() + 1;
        writer.Write(Encoding.ASCII.GetBytes($"xref\n0 {size}\n"));
        writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
        for (var objectId = 1; objectId < size; objectId++)
        {
            if (offsets.TryGetValue(objectId, out var offset))
            {
                writer.Write(Encoding.ASCII.GetBytes($"{offset:D10} 00000 n \n"));
            }
            else
            {
                writer.Write(Encoding.ASCII.GetBytes("0000000000 65535 f \n"));
            }
        }

        writer.Write(Encoding.ASCII.GetBytes($"trailer\n<< /Size {size} /Root {rootObject} 0 R /Info {infoObject} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] StreamObject(string dictionary, byte[] data)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes($"{dictionary[..^2]} /Length {data.Length} >>\nstream\n"));
        writer.Write(data);
        writer.Write(Encoding.ASCII.GetBytes("\nendstream"));
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));

    [Fact]
    public void DetectFlavours_Returns_Both_PdfA_And_PdfUA_From_SamplePdf()
    {
        var bytes = BuildSamplePdf(); // contains pdfaid:part=4, pdfaid:conformance=B, pdfuaid:part=1
        var flavours = PdfLexerValidationParser.DetectFlavours(bytes);
        // PDF/A-4B is not a standard level, so only PDF/UA-1 is detected
        Assert.Contains(PDFAFlavour.PDFUA1, flavours);
    }

    [Fact]
    public void DetectFlavours_Returns_Both_PdfA_And_PdfUA()
    {
        var bytes = BuildPdfWithXmp("""
      <pdfaid:part>2</pdfaid:part>
      <pdfaid:conformance>B</pdfaid:conformance>
      <pdfuaid:part>1</pdfuaid:part>
""");
        var flavours = PdfLexerValidationParser.DetectFlavours(bytes);
        Assert.Contains(PDFAFlavour.PDFA2B, flavours);
        Assert.Contains(PDFAFlavour.PDFUA1, flavours);
        Assert.Equal(2, flavours.Count);
    }

    [Fact]
    public void DetectFlavour_Returns_PdfUA2()
    {
        var bytes = BuildPdfWithXmp("""
      <pdfuaid:part>2</pdfuaid:part>
""");
        var flavour = PdfLexerValidationParser.DetectFlavour(bytes);
        Assert.Equal(PDFAFlavour.PDFUA2, flavour);
    }

    [Fact]
    public void DetectFlavour_Returns_NoFlavour_Without_Metadata()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            [2] = Ascii("<< /Type /Pages /Count 0 /Kids [] >>"),
            [3] = Ascii("<< /Producer (Test) >>"),
        };
        var bytes = BuildPdf(objects, rootObject: 1, infoObject: 3);
        var flavour = PdfLexerValidationParser.DetectFlavour(bytes);
        Assert.Equal(PDFAFlavour.NoFlavour, flavour);
    }

    [Theory]
    [InlineData("1", "A", PDFAFlavour.PDFA1A)]
    [InlineData("1", "B", PDFAFlavour.PDFA1B)]
    [InlineData("2", "A", PDFAFlavour.PDFA2A)]
    [InlineData("2", "B", PDFAFlavour.PDFA2B)]
    [InlineData("2", "U", PDFAFlavour.PDFA2U)]
    [InlineData("3", "B", PDFAFlavour.PDFA3B)]
    [InlineData("4", "", PDFAFlavour.PDFA4)]
    [InlineData("4", "F", PDFAFlavour.PDFA4F)]
    [InlineData("4", "E", PDFAFlavour.PDFA4E)]
    public void DetectFlavour_Maps_PdfA_Part_And_Conformance(string part, string conformance, PDFAFlavour expected)
    {
        var conformanceXml = string.IsNullOrEmpty(conformance) ? "" : $"\n      <pdfaid:conformance>{conformance}</pdfaid:conformance>";
        var bytes = BuildPdfWithXmp($"""
      <pdfaid:part>{part}</pdfaid:part>{conformanceXml}
""");
        var flavour = PdfLexerValidationParser.DetectFlavour(bytes);
        Assert.Equal(expected, flavour);
    }

    private static byte[] BuildPdfWithXmp(string identificationElements)
    {
        var metadataXml = $"""
<?xpacket begin="\u00ef\u00bb\u00bf" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/">
  <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
    <rdf:Description rdf:about=""
      xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/"
      xmlns:pdfuaid="http://www.aiim.org/pdfua/ns/id/">
{identificationElements}
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end="w"?>
""";
        var objects = new Dictionary<int, byte[]>
        {
            [1] = Ascii($"<< /Type /Catalog /Pages 2 0 R /Metadata 3 0 R >>"),
            [2] = Ascii("<< /Type /Pages /Count 0 /Kids [] >>"),
            [3] = StreamObject("<< /Type /Metadata /Subtype /XML >>", Encoding.UTF8.GetBytes(metadataXml)),
            [4] = Ascii("<< /Producer (Test) >>"),
        };
        return BuildPdf(objects, rootObject: 1, infoObject: 4);
    }
}

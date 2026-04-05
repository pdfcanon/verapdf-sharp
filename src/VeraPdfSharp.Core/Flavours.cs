using System.Collections.ObjectModel;

namespace VeraPdfSharp.Core;

public enum PDFAFlavour
{
    NoFlavour,
    PDFA1A,
    PDFA1B,
    PDFA2A,
    PDFA2B,
    PDFA2U,
    PDFA3A,
    PDFA3B,
    PDFA3U,
    PDFA4,
    PDFA4F,
    PDFA4E,
    PDFUA1,
    PDFUA2,
}

public enum Specification
{
    NoStandard,
    Iso14289_1,
    Iso14289_2,
    Iso19005_1,
    Iso19005_2,
    Iso19005_3,
    Iso19005_4,
}

public enum SpecificationFamily
{
    None,
    PdfA,
    PdfUa,
}

public enum ConformanceLevel
{
    None,
    A,
    B,
    U,
    E,
    F,
}

public enum PdfBaseSpecification
{
    None,
    Pdf14,
    Iso32000_1,
    Iso32000_2,
}

public sealed record FlavourMetadata(
    PDFAFlavour Flavour,
    string Id,
    Specification Specification,
    SpecificationFamily Family,
    ConformanceLevel Level,
    PdfBaseSpecification BaseSpecification,
    string DisplayName);

public static class PDFAFlavours
{
    private static readonly ReadOnlyDictionary<PDFAFlavour, FlavourMetadata> MetadataByFlavour =
        new(new Dictionary<PDFAFlavour, FlavourMetadata>
        {
            [PDFAFlavour.NoFlavour] = new(PDFAFlavour.NoFlavour, "none", Specification.NoStandard, SpecificationFamily.None, ConformanceLevel.None, PdfBaseSpecification.None, "No flavour"),
            [PDFAFlavour.PDFA1A] = new(PDFAFlavour.PDFA1A, "1a", Specification.Iso19005_1, SpecificationFamily.PdfA, ConformanceLevel.A, PdfBaseSpecification.Pdf14, "PDF/A-1A"),
            [PDFAFlavour.PDFA1B] = new(PDFAFlavour.PDFA1B, "1b", Specification.Iso19005_1, SpecificationFamily.PdfA, ConformanceLevel.B, PdfBaseSpecification.Pdf14, "PDF/A-1B"),
            [PDFAFlavour.PDFA2A] = new(PDFAFlavour.PDFA2A, "2a", Specification.Iso19005_2, SpecificationFamily.PdfA, ConformanceLevel.A, PdfBaseSpecification.Iso32000_1, "PDF/A-2A"),
            [PDFAFlavour.PDFA2B] = new(PDFAFlavour.PDFA2B, "2b", Specification.Iso19005_2, SpecificationFamily.PdfA, ConformanceLevel.B, PdfBaseSpecification.Iso32000_1, "PDF/A-2B"),
            [PDFAFlavour.PDFA2U] = new(PDFAFlavour.PDFA2U, "2u", Specification.Iso19005_2, SpecificationFamily.PdfA, ConformanceLevel.U, PdfBaseSpecification.Iso32000_1, "PDF/A-2U"),
            [PDFAFlavour.PDFA3A] = new(PDFAFlavour.PDFA3A, "3a", Specification.Iso19005_3, SpecificationFamily.PdfA, ConformanceLevel.A, PdfBaseSpecification.Iso32000_1, "PDF/A-3A"),
            [PDFAFlavour.PDFA3B] = new(PDFAFlavour.PDFA3B, "3b", Specification.Iso19005_3, SpecificationFamily.PdfA, ConformanceLevel.B, PdfBaseSpecification.Iso32000_1, "PDF/A-3B"),
            [PDFAFlavour.PDFA3U] = new(PDFAFlavour.PDFA3U, "3u", Specification.Iso19005_3, SpecificationFamily.PdfA, ConformanceLevel.U, PdfBaseSpecification.Iso32000_1, "PDF/A-3U"),
            [PDFAFlavour.PDFA4] = new(PDFAFlavour.PDFA4, "4", Specification.Iso19005_4, SpecificationFamily.PdfA, ConformanceLevel.None, PdfBaseSpecification.Iso32000_2, "PDF/A-4"),
            [PDFAFlavour.PDFA4F] = new(PDFAFlavour.PDFA4F, "4f", Specification.Iso19005_4, SpecificationFamily.PdfA, ConformanceLevel.F, PdfBaseSpecification.Iso32000_2, "PDF/A-4F"),
            [PDFAFlavour.PDFA4E] = new(PDFAFlavour.PDFA4E, "4e", Specification.Iso19005_4, SpecificationFamily.PdfA, ConformanceLevel.E, PdfBaseSpecification.Iso32000_2, "PDF/A-4E"),
            [PDFAFlavour.PDFUA1] = new(PDFAFlavour.PDFUA1, "ua1", Specification.Iso14289_1, SpecificationFamily.PdfUa, ConformanceLevel.None, PdfBaseSpecification.Iso32000_1, "PDF/UA-1"),
            [PDFAFlavour.PDFUA2] = new(PDFAFlavour.PDFUA2, "ua2", Specification.Iso14289_2, SpecificationFamily.PdfUa, ConformanceLevel.None, PdfBaseSpecification.Iso32000_2, "PDF/UA-2"),
        });

    private static readonly ReadOnlyDictionary<string, PDFAFlavour> FlavourById =
        new(MetadataByFlavour.Values.ToDictionary(static x => x.Id, static x => x.Flavour, StringComparer.OrdinalIgnoreCase));

    private static readonly ReadOnlyDictionary<string, PDFAFlavour> FlavourByXmlName =
        new(new Dictionary<string, PDFAFlavour>(StringComparer.OrdinalIgnoreCase)
        {
            ["PDFA_1_A"] = PDFAFlavour.PDFA1A,
            ["PDFA_1_B"] = PDFAFlavour.PDFA1B,
            ["PDFA_2_A"] = PDFAFlavour.PDFA2A,
            ["PDFA_2_B"] = PDFAFlavour.PDFA2B,
            ["PDFA_2_U"] = PDFAFlavour.PDFA2U,
            ["PDFA_3_A"] = PDFAFlavour.PDFA3A,
            ["PDFA_3_B"] = PDFAFlavour.PDFA3B,
            ["PDFA_3_U"] = PDFAFlavour.PDFA3U,
            ["PDFA_4"] = PDFAFlavour.PDFA4,
            ["PDFA_4_F"] = PDFAFlavour.PDFA4F,
            ["PDFA_4_E"] = PDFAFlavour.PDFA4E,
            ["PDFUA_1"] = PDFAFlavour.PDFUA1,
            ["PDFUA_2"] = PDFAFlavour.PDFUA2,
        });

    public static IReadOnlyCollection<PDFAFlavour> InitialFlavours { get; } =
        new[]
        {
            PDFAFlavour.PDFA1B,
            PDFAFlavour.PDFA2B,
            PDFAFlavour.PDFA4,
            PDFAFlavour.PDFUA1,
        };

    public static FlavourMetadata GetMetadata(this PDFAFlavour flavour) => MetadataByFlavour[flavour];

    public static string GetId(this PDFAFlavour flavour) => flavour.GetMetadata().Id;

    public static PDFAFlavour ByFlavourId(string flavourId) =>
        FlavourById.TryGetValue(flavourId, out var flavour) ? flavour : PDFAFlavour.NoFlavour;

    public static PDFAFlavour FromXmlName(string xmlName) =>
        FlavourByXmlName.TryGetValue(xmlName, out var flavour) ? flavour : PDFAFlavour.NoFlavour;

    public static PDFAFlavour FromString(string input)
    {
        foreach (var pair in FlavourById)
        {
            if (input.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return PDFAFlavour.NoFlavour;
    }
}

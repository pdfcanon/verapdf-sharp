using VeraPdfSharp.Core;

namespace VeraPdfSharp.Tests;

public sealed class CoreTests
{
    [Fact]
    public void BuiltInProfiles_LoadExpectedInitialFlavours()
    {
        var directory = Profiles.GetVeraProfileDirectory();

        Assert.Contains(directory.Profiles, x => x.Flavour == PDFAFlavour.PDFA1B);
        Assert.Contains(directory.Profiles, x => x.Flavour == PDFAFlavour.PDFA2B);
        Assert.Contains(directory.Profiles, x => x.Flavour == PDFAFlavour.PDFA4);
        Assert.Contains(directory.Profiles, x => x.Flavour == PDFAFlavour.PDFUA1);
    }
}

using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Validation;

namespace VeraPdfSharp.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void Validator_ProducesFailedRuleForBrokenObject()
    {
        var rule = new Rule(
            new RuleId(Specification.Iso19005_1, "6.1.2", 1),
            "CosDocument",
            false,
            new HashSet<string>(),
            "Header must start at zero",
            "headerOffset == 0",
            new ErrorDetails("Header offset was %1", new[] { new ErrorArgument("headerOffset", "headerOffset") }),
            Array.Empty<Reference>());

        var profile = new ValidationProfile(
            PDFAFlavour.PDFA1B,
            new ProfileDetails("Test", "Test profile", "Tests", DateTimeOffset.UtcNow),
            string.Empty,
            new[] { rule },
            Array.Empty<Variable>());

        var parser = new FakeParser(new FakeModelObject("CosDocument") { ["headerOffset"] = 7 }, PDFAFlavour.PDFA1B);
        var validator = new BaseValidator(new[] { profile }, new ValidatorOptions(ShowErrorMessages: true));

        var result = validator.Validate(parser);

        Assert.False(result.IsCompliant);
        Assert.Single(result.FailedChecks);
        Assert.Single(result.TestAssertions);
        Assert.Contains("7", result.TestAssertions[0].ErrorMessage);
    }
}

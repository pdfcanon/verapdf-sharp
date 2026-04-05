using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Scripting;

namespace VeraPdfSharp.Tests;

public sealed class ScriptingTests
{
    [Fact]
    public void EvaluateRule_UsesObjectPropertiesWithoutHardcodingRuleLogic()
    {
        var obj = new FakeModelObject("CosDocument");
        obj.Set("headerOffset", 0);
        obj.Set("header", "%PDF-1.7");

        var rule = new Rule(
            new RuleId(Specification.Iso19005_1, "6.1.2", 1),
            "CosDocument",
            false,
            new HashSet<string>(),
            "Header must be present",
            "headerOffset == 0 && /%PDF-\\d\\.\\d/.test(header)",
            new ErrorDetails("Invalid header", Array.Empty<ErrorArgument>()),
            Array.Empty<Reference>());

        using var evaluator = new JavaScriptEvaluator();
        var passed = evaluator.EvaluateRule(obj, rule);

        Assert.True(passed);
    }

    [Fact]
    public void EvaluateVariable_CanReadAndWriteSharedState()
    {
        var obj = new FakeModelObject("ICCOutputProfile");
        obj.Set("S", "GTS_PDFA1");
        obj.Set("colorSpace", "RGB");

        using var evaluator = new JavaScriptEvaluator();
        evaluator.SetVariable("gOutputCS", null);
        var variable = new Variable("gOutputCS", "ICCOutputProfile", "null", "S == \"GTS_PDFA1\" ? colorSpace : gOutputCS");

        var value = evaluator.EvaluateVariable(variable, obj);

        Assert.Equal("RGB", value);
    }

    [Fact]
    public void EvaluateRule_ArrowFunctionFilter_WorksCorrectly()
    {
        // Rule 7.4.4/1: kidsStandardTypes.split('&').filter(elem => elem == 'H').length <= 1
        var obj = new FakeModelObject("PDStructElem");
        obj.Set("kidsStandardTypes", "P&H&P");

        var rule = new Rule(
            new RuleId(Specification.Iso14289_1, "7.4.4", 1),
            "PDStructElem",
            false,
            new HashSet<string>(),
            "Each node shall contain at most one child H tag",
            "kidsStandardTypes.split('&').filter(elem => elem == 'H').length <= 1",
            new ErrorDetails("Too many H", Array.Empty<ErrorArgument>()),
            Array.Empty<Reference>());

        using var evaluator = new JavaScriptEvaluator();
        evaluator.SetVariable("gContainsCatalogLang", false);
        evaluator.SetVariable("usesH", false);
        evaluator.SetVariable("usesHn", false);

        var passed = evaluator.EvaluateRule(obj, rule);
        Assert.True(passed, "Single H child should pass");
    }

    [Fact]
    public void EvaluateRule_ArrowFunctionFilter_FailsWithTwoH()
    {
        var obj = new FakeModelObject("PDStructElem");
        obj.Set("kidsStandardTypes", "H&H&P");

        var rule = new Rule(
            new RuleId(Specification.Iso14289_1, "7.4.4", 1),
            "PDStructElem",
            false,
            new HashSet<string>(),
            "Each node shall contain at most one child H tag",
            "kidsStandardTypes.split('&').filter(elem => elem == 'H').length <= 1",
            new ErrorDetails("Too many H", Array.Empty<ErrorArgument>()),
            Array.Empty<Reference>());

        using var evaluator = new JavaScriptEvaluator();
        evaluator.SetVariable("gContainsCatalogLang", false);
        evaluator.SetVariable("usesH", false);

        var passed = evaluator.EvaluateRule(obj, rule);
        Assert.False(passed, "Two H children should fail");
    }

    [Fact]
    public void EvaluateRule_ArrowFunctionFilter_NoH_Passes()
    {
        var obj = new FakeModelObject("PDStructElem");
        obj.Set("kidsStandardTypes", "P&Span&Table");

        var rule = new Rule(
            new RuleId(Specification.Iso14289_1, "7.4.4", 1),
            "PDStructElem",
            false,
            new HashSet<string>(),
            "Each node shall contain at most one child H tag",
            "kidsStandardTypes.split('&').filter(elem => elem == 'H').length <= 1",
            new ErrorDetails("Too many H", Array.Empty<ErrorArgument>()),
            Array.Empty<Reference>());

        using var evaluator = new JavaScriptEvaluator();
        evaluator.SetVariable("gContainsCatalogLang", false);
        evaluator.SetVariable("usesH", false);

        var passed = evaluator.EvaluateRule(obj, rule);
        Assert.True(passed, "No H children should pass");
    }
}

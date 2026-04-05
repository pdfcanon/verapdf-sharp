using System.Runtime.CompilerServices;
using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Scripting;

namespace VeraPdfSharp.Validation;

public interface IPDFAValidator
{
    ValidationProfile? GetProfile();
    ValidationResult Validate(IValidationParser parser);
    IReadOnlyList<ValidationResult> ValidateAll(IValidationParser parser);
}

public sealed record ValidatorOptions(
    bool LogPassedChecks = false,
    int MaxFailures = 0,
    int MaxNumberOfDisplayedFailedChecks = 100,
    bool ShowErrorMessages = false);

public static class ValidatorFactory
{
    public static IPDFAValidator CreateValidator(PDFAFlavour flavour, bool logPassedChecks = false) =>
        new BaseValidator(new[] { Profiles.GetVeraProfileDirectory().GetValidationProfileByFlavour(flavour) }, new ValidatorOptions(logPassedChecks));

    public static IPDFAValidator CreateValidator(IEnumerable<PDFAFlavour> flavours, ValidatorOptions? options = null) =>
        new BaseValidator(Profiles.GetVeraProfileDirectory().GetValidationProfilesByFlavours(flavours), options ?? new ValidatorOptions());
}

public sealed class BaseValidator : IPDFAValidator
{
    private readonly IReadOnlyList<ValidationProfile> _profiles;
    private readonly ValidatorOptions _options;

    public BaseValidator(IReadOnlyList<ValidationProfile> profiles, ValidatorOptions options)
    {
        _profiles = profiles;
        _options = options;
    }

    public ValidationProfile? GetProfile() => _profiles.FirstOrDefault();

    public ValidationResult Validate(IValidationParser parser) => ValidateAll(parser).First();

    public IReadOnlyList<ValidationResult> ValidateAll(IValidationParser parser)
    {
        parser.SetFlavours(_profiles.Select(static x => x.Flavour));
        var root = parser.GetRoot();
        using var evaluator = new JavaScriptEvaluator();
        var states = _profiles.Select(profile => new FlavourState(profile)).ToArray();

        InitializeVariables(evaluator, states);
        var stack = new Stack<(IModelObject Object, string Context)>();
        var seen = new HashSet<IModelObject>(ReferenceEqualityComparer.Instance);
        stack.Push((root, "root"));

        while (stack.Count > 0)
        {
            var (current, context) = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            EvaluateRules(evaluator, current, context, root.ObjectType, states);
            UpdateVariables(evaluator, current, states);

            foreach (var link in current.Links.Reverse())
            {
                var linked = current.GetLinkedObjects(link);
                for (var index = linked.Count - 1; index >= 0; index--)
                {
                    stack.Push((linked[index], $"{context}/{link}[{index}]"));
                }
            }
        }

        foreach (var state in states)
        {
            foreach (var deferred in state.DeferredRules)
            {
                foreach (var item in deferred.Value)
                {
                    EvaluateRule(evaluator, state, item.Object, item.Context, deferred.Key, root.ObjectType);
                }
            }
        }

        return states.Select(static state => state.ToResult()).ToArray();
    }

    private static void InitializeVariables(JavaScriptEvaluator evaluator, IEnumerable<FlavourState> states)
    {
        foreach (var variable in states.SelectMany(static x => x.Profile.Variables))
        {
            evaluator.SetVariable(variable.Name, evaluator.EvaluateString(variable.DefaultValue));
        }
    }

    private static void UpdateVariables(JavaScriptEvaluator evaluator, IModelObject current, IEnumerable<FlavourState> states)
    {
        foreach (var state in states)
        {
            foreach (var variable in state.Profile.GetVariablesByObject(current.ObjectType))
            {
                evaluator.SetVariable(variable.Name, evaluator.EvaluateVariable(variable, current));
            }

            foreach (var superType in current.SuperTypes)
            {
                foreach (var variable in state.Profile.GetVariablesByObject(superType))
                {
                    evaluator.SetVariable(variable.Name, evaluator.EvaluateVariable(variable, current));
                }
            }
        }
    }

    private void EvaluateRules(JavaScriptEvaluator evaluator, IModelObject current, string context, string rootType, IEnumerable<FlavourState> states)
    {
        foreach (var state in states)
        {
            foreach (var rule in state.Profile.GetRulesByObject(current.ObjectType))
            {
                QueueOrEvaluate(evaluator, state, current, context, rule, rootType);
            }

            foreach (var superType in current.SuperTypes)
            {
                foreach (var rule in state.Profile.GetRulesByObject(superType))
                {
                    QueueOrEvaluate(evaluator, state, current, context, rule, rootType);
                }
            }
        }
    }

    private void QueueOrEvaluate(JavaScriptEvaluator evaluator, FlavourState state, IModelObject current, string context, Rule rule, string rootType)
    {
        if (rule.Deferred)
        {
            if (!state.DeferredRules.TryGetValue(rule, out var list))
            {
                list = new List<DeferredRuleItem>();
                state.DeferredRules[rule] = list;
            }

            list.Add(new DeferredRuleItem(current, context));
            return;
        }

        EvaluateRule(evaluator, state, current, context, rule, rootType);
    }

    private void EvaluateRule(JavaScriptEvaluator evaluator, FlavourState state, IModelObject current, string context, Rule rule, string rootType)
    {
        if (_options.MaxFailures > 0 && state.TotalFailures >= _options.MaxFailures)
        {
            return;
        }

        bool passed;
        try
        {
            passed = evaluator.EvaluateRule(current, rule);
        }
        catch (Exception ex)
        {
            passed = false;
            state.RuntimeErrors.Add($"{rule.RuleId}: {ex.Message}");
        }

        state.TotalAssertions++;
        if (!passed)
        {
            state.IsCompliant = false;
            state.TotalFailures++;
            state.FailedChecks[rule.RuleId] = state.FailedChecks.TryGetValue(rule.RuleId, out var existing) ? existing + 1 : 1;

            if (_options.MaxNumberOfDisplayedFailedChecks == -1 || state.FailedChecks[rule.RuleId] <= _options.MaxNumberOfDisplayedFailedChecks)
            {
                var errorArguments = _options.ShowErrorMessages
                    ? evaluator.EvaluateErrorArguments(current, rule.Error.Arguments)
                    : Array.Empty<ErrorArgument>();
                state.Assertions.Add(new TestAssertion(
                    state.TotalAssertions,
                    rule.RuleId,
                    TestAssertionStatus.Failed,
                    rule.Description,
                    new Location(rootType, context),
                    current.Context,
                    _options.ShowErrorMessages ? FormatError(rule.Error.Message, errorArguments) : null,
                    errorArguments));
            }

            return;
        }

        if (_options.LogPassedChecks)
        {
            state.Assertions.Add(new TestAssertion(
                state.TotalAssertions,
                rule.RuleId,
                TestAssertionStatus.Passed,
                rule.Description,
                new Location(rootType, context),
                current.Context,
                null,
                Array.Empty<ErrorArgument>()));
        }
    }

    private static string FormatError(string template, IReadOnlyList<ErrorArgument> arguments)
    {
        var result = template;
        for (var index = arguments.Count - 1; index >= 0; index--)
        {
            var argument = arguments[index];
            var value = argument.Value ?? "null";
            result = result.Replace($"%{argument.Name}%", value, StringComparison.Ordinal);
            result = result.Replace($"%{index + 1}", value, StringComparison.Ordinal);
        }

        return result;
    }

    private sealed class FlavourState
    {
        public FlavourState(ValidationProfile profile)
        {
            Profile = profile;
        }

        public ValidationProfile Profile { get; }
        public List<TestAssertion> Assertions { get; } = new();
        public Dictionary<RuleId, int> FailedChecks { get; } = new();
        public Dictionary<Rule, List<DeferredRuleItem>> DeferredRules { get; } = new();
        public List<string> RuntimeErrors { get; } = new();
        public bool IsCompliant { get; set; } = true;
        public int TotalAssertions { get; set; }
        public int TotalFailures { get; set; }

        public ValidationResult ToResult() =>
            new(
                IsCompliant,
                Profile.Flavour,
                Profile.Details,
                TotalAssertions,
                Assertions.ToArray(),
                Profile,
                RuntimeErrors.Count == 0 ? JobEndStatus.Normal : JobEndStatus.Failed,
                FailedChecks,
                RuntimeErrors.ToArray());
    }

    private sealed record DeferredRuleItem(IModelObject Object, string Context);
}

using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace VeraPdfSharp.Core;

public sealed record ProfileDetails(string Name, string Description, string Creator, DateTimeOffset Created);

public sealed record Reference(string Specification, string Clause);

public sealed record RuleId(Specification Specification, string Clause, int TestNumber)
{
    public override string ToString() => $"{Specification}:{Clause}:{TestNumber}";
}

public sealed record ErrorArgument(string Argument, string Name, string? Value = null);

public sealed record ErrorDetails(string Message, IReadOnlyList<ErrorArgument> Arguments);

public sealed record Rule(
    RuleId RuleId,
    string Object,
    bool Deferred,
    IReadOnlySet<string> Tags,
    string Description,
    string Test,
    ErrorDetails Error,
    IReadOnlyList<Reference> References);

public sealed record Variable(string Name, string Object, string DefaultValue, string Value);

public sealed class ValidationProfile
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Rule>> _rulesByObject;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Variable>> _variablesByObject;

    public ValidationProfile(
        PDFAFlavour flavour,
        ProfileDetails details,
        string hexSha1Digest,
        IReadOnlyList<Rule> rules,
        IReadOnlyList<Variable> variables)
    {
        Flavour = flavour;
        Details = details;
        HexSha1Digest = hexSha1Digest;
        Rules = rules;
        Variables = variables;
        Tags = rules.SelectMany(static x => x.Tags).Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        _rulesByObject = rules.GroupBy(static x => x.Object, StringComparer.Ordinal)
            .ToDictionary(static x => x.Key, static x => (IReadOnlyList<Rule>)x.ToArray(), StringComparer.Ordinal);
        _variablesByObject = variables.GroupBy(static x => x.Object, StringComparer.Ordinal)
            .ToDictionary(static x => x.Key, static x => (IReadOnlyList<Variable>)x.ToArray(), StringComparer.Ordinal);
    }

    public PDFAFlavour Flavour { get; }
    public ProfileDetails Details { get; }
    public string HexSha1Digest { get; }
    public IReadOnlyList<Rule> Rules { get; }
    public IReadOnlyList<Variable> Variables { get; }
    public IReadOnlyList<string> Tags { get; }

    public Rule? GetRuleByRuleId(RuleId id) => Rules.FirstOrDefault(x => x.RuleId == id);

    public IReadOnlyList<Rule> GetRulesByObject(string objectName) =>
        _rulesByObject.TryGetValue(objectName, out var rules) ? rules : Array.Empty<Rule>();

    public IReadOnlyList<Variable> GetVariablesByObject(string objectName) =>
        _variablesByObject.TryGetValue(objectName, out var variables) ? variables : Array.Empty<Variable>();
}

public sealed class ProfileDirectory
{
    private readonly IReadOnlyDictionary<PDFAFlavour, ValidationProfile> _profiles;

    public ProfileDirectory(IEnumerable<ValidationProfile> profiles)
    {
        _profiles = profiles.ToDictionary(static x => x.Flavour);
    }

    public IReadOnlyCollection<ValidationProfile> Profiles => _profiles.Values.ToArray();

    public ValidationProfile GetValidationProfileByFlavour(PDFAFlavour flavour) =>
        _profiles.TryGetValue(flavour, out var profile)
            ? profile
            : throw new KeyNotFoundException($"Validation profile for flavour '{flavour}' was not found.");

    public IReadOnlyList<ValidationProfile> GetValidationProfilesByFlavours(IEnumerable<PDFAFlavour> flavours) =>
        flavours.Select(GetValidationProfileByFlavour).ToArray();
}

public static class Profiles
{
    private static readonly Lazy<ProfileDirectory> BuiltInProfiles = new(LoadBuiltInProfiles);

    public static ProfileDirectory GetVeraProfileDirectory() => BuiltInProfiles.Value;

    public static ValidationProfile LoadProfile(Stream stream)
    {
        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidOperationException("Validation profile XML has no root element.");
        var flavour = PDFAFlavours.FromXmlName(root.Attribute("flavour")?.Value ?? string.Empty);

        var detailsElement = root.Element(root.Name.Namespace + "details")
            ?? throw new InvalidOperationException("Validation profile XML is missing the details element.");

        var details = new ProfileDetails(
            detailsElement.Element(root.Name.Namespace + "name")?.Value?.Trim() ?? string.Empty,
            detailsElement.Element(root.Name.Namespace + "description")?.Value?.Trim() ?? string.Empty,
            detailsElement.Attribute("creator")?.Value ?? string.Empty,
            DateTimeOffset.Parse(detailsElement.Attribute("created")?.Value ?? DateTimeOffset.MinValue.ToString("O", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

        var rules = root.Element(root.Name.Namespace + "rules")?
            .Elements(root.Name.Namespace + "rule")
            .Select(ParseRule)
            .ToArray() ?? Array.Empty<Rule>();

        var variables = root.Element(root.Name.Namespace + "variables")?
            .Elements(root.Name.Namespace + "variable")
            .Select(ParseVariable)
            .ToArray() ?? Array.Empty<Variable>();

        return new ValidationProfile(flavour, details, root.Element(root.Name.Namespace + "hash")?.Value ?? string.Empty, rules, variables);
    }

    private static Rule ParseRule(XElement element)
    {
        var namespaceName = element.Name.Namespace;
        var tagsRaw = element.Attribute("tags")?.Value ?? string.Empty;
        var tags = tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var idElement = element.Element(namespaceName + "id")
            ?? throw new InvalidOperationException("Rule XML is missing the id element.");
        var specification = ParseSpecification(idElement.Attribute("specification")?.Value ?? string.Empty);
        var arguments = element.Element(namespaceName + "error")?
            .Element(namespaceName + "arguments")?
            .Elements(namespaceName + "argument")
            .Select(static (x, index) => new ErrorArgument(x.Value.Trim(), x.Value.Trim(), null))
            .ToArray() ?? Array.Empty<ErrorArgument>();

        var references = element.Element(namespaceName + "references")?
            .Elements(namespaceName + "reference")
            .Select(static x => new Reference(x.Attribute("specification")?.Value ?? string.Empty, x.Attribute("clause")?.Value ?? string.Empty))
            .ToArray() ?? Array.Empty<Reference>();

        return new Rule(
            new RuleId(
                specification,
                idElement.Attribute("clause")?.Value ?? string.Empty,
                int.Parse(idElement.Attribute("testNumber")?.Value ?? "0", CultureInfo.InvariantCulture)),
            element.Attribute("object")?.Value ?? string.Empty,
            bool.TryParse(element.Attribute("deferred")?.Value, out var deferred) && deferred,
            tags,
            element.Element(namespaceName + "description")?.Value?.Trim() ?? string.Empty,
            element.Element(namespaceName + "test")?.Value?.Trim() ?? string.Empty,
            new ErrorDetails(
                element.Element(namespaceName + "error")?.Element(namespaceName + "message")?.Value?.Trim() ?? string.Empty,
                arguments),
            references);
    }

    private static Variable ParseVariable(XElement element)
    {
        var namespaceName = element.Name.Namespace;
        return new Variable(
            element.Attribute("name")?.Value ?? string.Empty,
            element.Attribute("object")?.Value ?? string.Empty,
            element.Element(namespaceName + "defaultValue")?.Value?.Trim() ?? "null",
            element.Element(namespaceName + "value")?.Value?.Trim() ?? "null");
    }

    private static Specification ParseSpecification(string raw) => raw switch
    {
        "ISO_14289_1" => Specification.Iso14289_1,
        "ISO_14289_2" => Specification.Iso14289_2,
        "ISO_19005_1" => Specification.Iso19005_1,
        "ISO_19005_2" => Specification.Iso19005_2,
        "ISO_19005_3" => Specification.Iso19005_3,
        "ISO_19005_4" => Specification.Iso19005_4,
        _ => Specification.NoStandard,
    };

    private static ProfileDirectory LoadBuiltInProfiles()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(static x => x.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && x.Contains(".Resources.Profiles.", StringComparison.Ordinal))
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();

        var profiles = new List<ValidationProfile>(resources.Length);
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded resource '{resource}' could not be opened.");
            profiles.Add(LoadProfile(stream));
        }

        return new ProfileDirectory(profiles);
    }
}

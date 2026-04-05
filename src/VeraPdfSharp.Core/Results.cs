namespace VeraPdfSharp.Core;

public enum TestAssertionStatus
{
    Passed,
    Failed,
}

public enum JobEndStatus
{
    Normal,
    Cancelled,
    Failed,
}

public sealed record Location(string ContextType, string Path);

public sealed record TestAssertion(
    int Ordinal,
    RuleId RuleId,
    TestAssertionStatus Status,
    string Description,
    Location Location,
    string? ObjectContext,
    string? ErrorMessage,
    IReadOnlyList<ErrorArgument> ErrorArguments);

public sealed record ValidationResult(
    bool IsCompliant,
    PDFAFlavour Flavour,
    ProfileDetails ProfileDetails,
    int TotalAssertions,
    IReadOnlyList<TestAssertion> TestAssertions,
    ValidationProfile ValidationProfile,
    JobEndStatus JobEndStatus,
    IReadOnlyDictionary<RuleId, int> FailedChecks,
    IReadOnlyList<string> RuntimeErrors = null!)
{
    public IReadOnlyList<string> RuntimeErrors { get; init; } = RuntimeErrors ?? Array.Empty<string>();
}

public sealed class ValidationException : Exception
{
    public ValidationException(string message)
        : base(message)
    {
    }

    public ValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Validation;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return;
}

if (args.Contains("--list-flavours", StringComparer.OrdinalIgnoreCase))
{
    foreach (var supportedFlavour in PDFAFlavours.InitialFlavours)
    {
        var metadata = supportedFlavour.GetMetadata();
        Console.WriteLine($"{metadata.Id,-4} {metadata.DisplayName}");
    }

    return;
}

var file = args[0];
if (!File.Exists(file))
{
    Console.Error.WriteLine($"File not found: {file}");
    Environment.ExitCode = 1;
    return;
}

var flavour = args.Length > 1 ? ResolveFlavour(args[1]) : PdfLexerValidationParser.DetectFlavour(file);
if (flavour == PDFAFlavour.NoFlavour)
{
    Console.Error.WriteLine("Could not detect flavour. Specify one explicitly or use --list-flavours to see supported values.");
    Environment.ExitCode = 1;
    return;
}

using var parser = PdfLexerValidationParser.FromFile(file, flavour);

var validator = ValidatorFactory.CreateValidator(new[] { flavour }, new ValidatorOptions(ShowErrorMessages: true));
var result = validator.Validate(parser);

Console.WriteLine($"File: {Path.GetFullPath(file)}");
Console.WriteLine($"Flavour: {result.Flavour.GetMetadata().DisplayName}");
Console.WriteLine($"Compliant: {result.IsCompliant}");
Console.WriteLine($"Assertions: {result.TotalAssertions}");
Console.WriteLine($"Failed rules: {result.FailedChecks.Count}");

foreach (var assertion in result.TestAssertions.Where(static x => x.Status == TestAssertionStatus.Failed).Take(25))
{
    Console.WriteLine($"- {assertion.RuleId} @ {assertion.Location.Path}");
    Console.WriteLine($"  {assertion.Description}");
    if (!string.IsNullOrWhiteSpace(assertion.ErrorMessage))
    {
        Console.WriteLine($"  {assertion.ErrorMessage}");
    }
}

static PDFAFlavour ResolveFlavour(string raw)
{
    var byId = PDFAFlavours.ByFlavourId(raw);
    if (byId != PDFAFlavour.NoFlavour)
    {
        return byId;
    }

    return PDFAFlavours.FromString(raw.Replace("/", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal));
}

static void PrintHelp()
{
    Console.WriteLine("VeraPdfSharp CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  VeraPdfSharp.Cli <file.pdf> [flavour]");
    Console.WriteLine("  VeraPdfSharp.Cli --list-flavours");
    Console.WriteLine();
    Console.WriteLine("When flavour is omitted, it is auto-detected from XMP metadata.");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  VeraPdfSharp.Cli sample.pdf");
    Console.WriteLine("  VeraPdfSharp.Cli sample.pdf 1b");
    Console.WriteLine("  VeraPdfSharp.Cli sample.pdf ua1");
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using VeraPdfSharp.Core;
using VeraPdfSharp.Model;

namespace VeraPdfSharp.Scripting;

public sealed class JavaScriptEvaluator : IDisposable
{
    private readonly Engine _engine;
    private readonly ConcurrentDictionary<string, string> _scriptCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalVariables = new(StringComparer.Ordinal);

    public JavaScriptEvaluator(TimeSpan? timeout = null, long memoryLimitBytes = 8_000_000, int maxStatements = 10_000)
    {
        _engine = new Engine(options =>
        {
            options.Strict();
            options.LimitMemory(memoryLimitBytes);
            options.TimeoutInterval(timeout ?? TimeSpan.FromSeconds(2));
            options.MaxStatements(maxStatements);
        });

        // veraPDF profiles use Java's List.contains() and String.contains() which don't exist in JS.
        // Polyfill to match Java semantics.
        _engine.Execute("if(!Array.prototype.contains){Array.prototype.contains=Array.prototype.includes}")
               .Execute("if(!String.prototype.contains){String.prototype.contains=String.prototype.includes}");
    }

    public object? EvaluateString(string source)
    {
        var value = _engine.Evaluate(source);
        return value.ToObject();
    }

    public object? EvaluateVariable(Variable variable, IModelObject obj)
    {
        var script = GetOrBuildScript(obj, variable.Value, wrapAsBoolean: false);
        _engine.SetValue("obj", obj);
        return _engine.Evaluate(script).ToObject();
    }

    public bool EvaluateRule(IModelObject obj, Rule rule)
    {
        var script = GetOrBuildScript(obj, rule.Test, wrapAsBoolean: true);
        _engine.SetValue("obj", obj);
        var result = _engine.Evaluate(script).ToObject();
        return result is bool boolean && boolean;
    }

    public IReadOnlyList<ErrorArgument> EvaluateErrorArguments(IModelObject obj, IReadOnlyList<ErrorArgument> arguments)
    {
        var result = new List<ErrorArgument>(arguments.Count);
        foreach (var argument in arguments)
        {
            var script = GetOrBuildScript(obj, argument.Argument, wrapAsBoolean: false);
            _engine.SetValue("obj", obj);
            var value = _engine.Evaluate(script).ToObject();
            result.Add(argument with { Value = FormatValue(value) });
        }

        return result;
    }

    public void SetVariable(string name, object? value)
    {
        _globalVariables.Add(name);
        _engine.SetValue(name, value);
    }

    public object? GetVariable(string name) => _engine.GetValue(name).ToObject();

    public void Dispose()
    {
    }

    private string GetOrBuildScript(IModelObject obj, string expression, bool wrapAsBoolean)
    {
        var key = $"{obj.ObjectType}|{wrapAsBoolean}|{expression}";
        return _scriptCache.GetOrAdd(key, _ => BuildScript(obj, expression, wrapAsBoolean, _globalVariables));
    }

    private static string BuildScript(IModelObject obj, string expression, bool wrapAsBoolean, HashSet<string> globalVariables)
    {
        var rewritten = expression;
        var builder = new StringBuilder();

        // Rewrite Java's String.length() to JS property access.
        rewritten = rewritten.Replace(".length()", ".length");

        foreach (var property in obj.Properties)
        {
            rewritten = RewriteToken(builder, rewritten, property, $"obj.GetPropertyValue(\"{property}\")");
        }

        foreach (var link in obj.Links)
        {
            var token = $"{link}_size";
            rewritten = RewriteToken(builder, rewritten, token, $"obj.GetLinkedObjects(\"{link}\").Count");
        }

        // Declare any remaining bare identifiers as null to avoid ReferenceError in strict mode.
        // This handles rule expressions that reference properties not set on the current object.
        DeclareUnknownIdentifiers(builder, rewritten, obj, globalVariables);

        builder.Append("function __verapdf_eval(){ return ");
        if (wrapAsBoolean)
        {
            builder.Append('(').Append(rewritten).Append(") === true");
        }
        else
        {
            builder.Append(rewritten);
        }

        builder.Append("; }\n__verapdf_eval();");
        return builder.ToString();
    }

    private static string RewriteToken(StringBuilder preamble, string expression, string token, string replacement)
    {
        var count = CountOccurrences(expression, token);
        if (count > 1)
        {
            preamble.Append("var ").Append(token).Append(" = ").Append(replacement).Append(";\n");
            return expression;
        }

        return count == 1
            ? Regex.Replace(expression, $@"(?<!\w){Regex.Escape(token)}(?!\w)", replacement, RegexOptions.CultureInvariant)
            : expression;
    }

    private static int CountOccurrences(string expression, string token) =>
        Regex.Matches(expression, $@"(?<!\w){Regex.Escape(token)}(?!\w)", RegexOptions.CultureInvariant).Count;

    private static readonly HashSet<string> JsReservedWords = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "undefined", "NaN", "Infinity",
        "typeof", "instanceof", "void", "delete", "new", "this",
        "if", "else", "for", "while", "do", "switch", "case", "default",
        "break", "continue", "return", "throw", "try", "catch", "finally",
        "var", "let", "const", "function", "class", "import", "export",
        "in", "of", "with", "yield", "await", "async", "debugger",
        "Math", "String", "Number", "Boolean", "Array", "Object",
        "parseInt", "parseFloat", "isNaN", "isFinite",
        "obj", "__verapdf_eval",
    };

    private static readonly Regex IdentifierRegex = new(@"(?<!\.)\b([a-zA-Z_]\w*)\b", RegexOptions.Compiled);

    private static void DeclareUnknownIdentifiers(StringBuilder preamble, string expression, IModelObject obj, HashSet<string> globalVariables)
    {
        var known = new HashSet<string>(obj.Properties, StringComparer.Ordinal);
        foreach (var link in obj.Links)
            known.Add($"{link}_size");

        foreach (Match match in IdentifierRegex.Matches(expression))
        {
            var id = match.Value;
            if (JsReservedWords.Contains(id) || known.Contains(id) || globalVariables.Contains(id))
                continue;

            // Check if this identifier still appears as a bare token (not already
            // rewritten into obj.GetPropertyValue("...") or similar).
            if (CountOccurrences(expression, id) > 0)
            {
                preamble.Append("var ").Append(id).Append(" = obj.GetPropertyValue(\"").Append(id).Append("\");\n");
                known.Add(id); // avoid duplicate declarations
            }
        }
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        string text when text.Length == 0 || string.Equals(text, "null", StringComparison.Ordinal) => $"\"{text}\"",
        double number when Math.Abs(number - Math.Floor(number)) < 1e-7 => ((int)number).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
    };
}

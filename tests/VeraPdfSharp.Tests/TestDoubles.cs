using VeraPdfSharp.Core;
using VeraPdfSharp.Model;

namespace VeraPdfSharp.Tests;

internal sealed class FakeModelObject : ModelObjectBase
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public FakeModelObject(string objectType, params string[] superTypes)
        : base(objectType, superTypes: superTypes)
    {
    }

    public object? this[string key]
    {
        set => Set(key, value);
    }

    public void Set(string key, object? value)
    {
        _values[key] = value;
        SetProperty(key, value);
    }
}

internal sealed class FakeParser : IValidationParser
{
    private readonly IModelObject _root;
    private IReadOnlyList<PDFAFlavour> _flavours;

    public FakeParser(IModelObject root, PDFAFlavour flavour)
    {
        _root = root;
        Flavour = flavour;
        _flavours = new[] { flavour };
    }

    public PDFAFlavour Flavour { get; }
    public IReadOnlyList<PDFAFlavour> Flavours => _flavours;

    public IModelObject GetRoot() => _root;

    public void SetFlavours(IEnumerable<PDFAFlavour> flavours) => _flavours = flavours.ToArray();

    public void Dispose()
    {
    }
}

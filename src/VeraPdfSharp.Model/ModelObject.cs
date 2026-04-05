using System.Collections.ObjectModel;

namespace VeraPdfSharp.Model;

public interface IModelObject
{
    string ObjectType { get; }
    IReadOnlyList<string> SuperTypes { get; }
    IReadOnlyCollection<string> Properties { get; }
    IReadOnlyCollection<string> Links { get; }
    string? Id { get; }
    string? Context { get; }
    string? ExtraContext { get; }
    object? GetPropertyValue(string name);
    IReadOnlyList<IModelObject> GetLinkedObjects(string name);
}

public abstract class ModelObjectBase : IModelObject
{
    private readonly Dictionary<string, object?> _properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<IModelObject>> _links = new(StringComparer.Ordinal);

    protected ModelObjectBase(string objectType, string? id = null, string? context = null, string? extraContext = null, params string[] superTypes)
    {
        ObjectType = objectType;
        Id = id;
        Context = context;
        ExtraContext = extraContext;
        SuperTypes = Array.AsReadOnly(superTypes ?? Array.Empty<string>());
    }

    public string ObjectType { get; internal set; }
    public IReadOnlyList<string> SuperTypes { get; }
    public IReadOnlyCollection<string> Properties => new ReadOnlyCollection<string>(_properties.Keys.ToArray());
    public IReadOnlyCollection<string> Links => new ReadOnlyCollection<string>(_links.Keys.ToArray());
    public string? Id { get; }
    public string? Context { get; }
    public string? ExtraContext { get; }

    protected void SetProperty(string name, object? value) => _properties[name] = value;

    protected void SetLink(string name, IReadOnlyList<IModelObject> objects) => _links[name] = objects;

    public object? GetPropertyValue(string name) => _properties.TryGetValue(name, out var value) ? value : null;

    public IReadOnlyList<IModelObject> GetLinkedObjects(string name) =>
        _links.TryGetValue(name, out var value) ? value : Array.Empty<IModelObject>();
}

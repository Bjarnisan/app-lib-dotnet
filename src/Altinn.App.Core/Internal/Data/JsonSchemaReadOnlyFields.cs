using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Altinn.App.Core.Configuration;
using Altinn.App.Core.Features.Auth;
using Altinn.App.Core.Internal.App;
using Altinn.Platform.Storage.Interface.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Altinn.App.Core.Internal.Data;

/// <summary>
/// Keeps the stored value of fields marked <c>"readOnly": true</c> in the JSON schema of a data model when a client
/// changes them, see <see cref="AppSettings.EnforceJsonSchemaReadOnly"/>.
/// </summary>
internal sealed class JsonSchemaReadOnlyFields
{
    private readonly IAppResources _appResources;
    private readonly IAuthenticationContext _authenticationContext;
    private readonly IOptions<AppSettings> _appSettings;
    private readonly ILogger<JsonSchemaReadOnlyFields> _logger;
    private readonly ConcurrentDictionary<string, ReadOnlyNode?> _readOnlyNodes = new();

    public JsonSchemaReadOnlyFields(
        IAppResources appResources,
        IAuthenticationContext authenticationContext,
        IOptions<AppSettings> appSettings,
        ILogger<JsonSchemaReadOnlyFields> logger
    )
    {
        _appResources = appResources;
        _authenticationContext = authenticationContext;
        _appSettings = appSettings;
        _logger = logger;
    }

    /// <summary>
    /// Sets the read-only fields of <paramref name="currentModel"/> back to their values in <paramref name="previousModel"/>.
    /// </summary>
    /// <returns><paramref name="currentModel"/> when no read-only field was changed, otherwise a corrected copy</returns>
    public object RestoreReadOnlyFields(DataType dataType, object previousModel, object currentModel)
    {
        if (
            !_appSettings.Value.EnforceJsonSchemaReadOnly
            || _authenticationContext.Current is Authenticated.ServiceOwner
        )
        {
            return currentModel;
        }

        var readOnlyNode = _readOnlyNodes.GetOrAdd(dataType.Id, LoadReadOnlyNode);
        if (readOnlyNode is null)
        {
            return currentModel;
        }

        var previous = SerializeToJsonObject(previousModel);
        var current = SerializeToJsonObject(currentModel);
        var restoredFields = new List<string>();
        RestoreReadOnlyProperties(readOnlyNode, previous, current, string.Empty, restoredFields);
        if (restoredFields.Count == 0)
        {
            return currentModel;
        }

        _logger.LogWarning(
            "Kept the stored value of read-only fields {Fields} in data type {DataTypeId}",
            string.Join(", ", restoredFields),
            dataType.Id
        );
        // ! Deserializing a JSON object never returns null
        return current.Deserialize(currentModel.GetType())!;
    }

    private static JsonObject SerializeToJsonObject(object model)
    {
        // ! A data model always serializes to a JSON object, never to null
        return JsonSerializer.SerializeToNode(model)!.AsObject();
    }

    private ReadOnlyNode? LoadReadOnlyNode(string dataTypeId)
    {
        string schema;
        try
        {
            schema = _appResources.GetModelJsonSchema(dataTypeId);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogWarning(
                "No JSON schema found for data type {DataTypeId}, so no fields are kept read-only",
                dataTypeId
            );
            return null;
        }

        using var document = JsonDocument.Parse(schema);
        var listsWithReadOnlyFields = new List<string>();
        var readOnlyNode = ReadOnlyNode.FromSchema(document.RootElement, listsWithReadOnlyFields);
        if (listsWithReadOnlyFields.Count > 0)
        {
            _logger.LogWarning(
                "Read-only fields inside lists are not supported and can still be changed: {Lists} in data type {DataTypeId}",
                string.Join(", ", listsWithReadOnlyFields),
                dataTypeId
            );
        }

        return readOnlyNode;
    }

    private static void RestoreReadOnlyProperties(
        ReadOnlyNode node,
        JsonObject? previous,
        JsonObject current,
        string path,
        List<string> restoredFields
    )
    {
        foreach (var (name, child) in node.Properties)
        {
            JsonNode? previousValue = null;
            bool previousHasValue = previous?.TryGetPropertyValue(name, out previousValue) ?? false;
            current.TryGetPropertyValue(name, out var currentValue);
            var fieldPath = path.Length == 0 ? name : $"{path}.{name}";

            if (child.IsReadOnly)
            {
                if (!JsonNode.DeepEquals(previousValue, currentValue))
                {
                    if (previousHasValue)
                    {
                        current[name] = previousValue?.DeepClone();
                    }
                    else
                    {
                        // Nothing was stored, so the property gets its default value when deserialized
                        current.Remove(name);
                    }
                    restoredFields.Add(fieldPath);
                }
            }
            else if (currentValue is JsonObject currentObject)
            {
                RestoreReadOnlyProperties(child, previousValue as JsonObject, currentObject, fieldPath, restoredFields);
            }
            else if (previousValue is JsonObject previousObject)
            {
                // The client removed the object, but the read-only fields inside it must stay
                var restoredObject = new JsonObject();
                RestoreReadOnlyProperties(child, previousObject, restoredObject, fieldPath, restoredFields);
                if (restoredObject.Count > 0)
                {
                    current[name] = restoredObject;
                }
            }
        }
    }

    /// <summary>
    /// The parts of a JSON schema that lead to a property marked <c>"readOnly": true</c>.
    /// </summary>
    internal sealed class ReadOnlyNode
    {
        private static readonly string[] _combinationKeywords = ["allOf", "anyOf", "oneOf"];

        public bool IsReadOnly { get; private set; }

        public Dictionary<string, ReadOnlyNode> Properties { get; } = new();

        /// <summary>
        /// Returns the read-only properties of <paramref name="schema"/>, or null when it has none.
        /// </summary>
        /// <param name="schema">The root of a JSON schema</param>
        /// <param name="listsWithReadOnlyFields">Paths to lists with read-only fields inside their items, which are not supported</param>
        public static ReadOnlyNode? FromSchema(JsonElement schema, List<string> listsWithReadOnlyFields) =>
            FromSchema(schema, schema, [], listsWithReadOnlyFields, string.Empty);

        private static ReadOnlyNode? FromSchema(
            JsonElement root,
            JsonElement schema,
            HashSet<string> refsInPath,
            List<string> listsWithReadOnlyFields,
            string path
        )
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var node = new ReadOnlyNode();
            if (schema.TryGetProperty("readOnly", out var readOnly) && readOnly.ValueKind == JsonValueKind.True)
            {
                node.IsReadOnly = true;
                return node;
            }

            if (
                schema.TryGetProperty("$ref", out var reference)
                && reference.GetString() is { } referencePath
                && refsInPath.Add(referencePath)
            )
            {
                node.Merge(
                    FromSchema(root, ResolveReference(root, referencePath), refsInPath, listsWithReadOnlyFields, path)
                );
                refsInPath.Remove(referencePath);
            }

            foreach (var keyword in _combinationKeywords)
            {
                if (schema.TryGetProperty(keyword, out var subSchemas) && subSchemas.ValueKind == JsonValueKind.Array)
                {
                    foreach (var subSchema in subSchemas.EnumerateArray())
                    {
                        node.Merge(FromSchema(root, subSchema, refsInPath, listsWithReadOnlyFields, path));
                    }
                }
            }

            if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    var propertyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    var child = FromSchema(root, property.Value, refsInPath, listsWithReadOnlyFields, propertyPath);
                    if (child is not null)
                    {
                        node.MergeProperty(property.Name, child);
                    }
                }
            }

            if (
                schema.TryGetProperty("items", out var items)
                && FromSchema(root, items, refsInPath, [], $"{path}[]") is not null
            )
            {
                listsWithReadOnlyFields.Add($"{path}[]");
            }

            return node.IsReadOnly || node.Properties.Count > 0 ? node : null;
        }

        private static JsonElement ResolveReference(JsonElement root, string referencePath)
        {
            if (!referencePath.StartsWith("#/", StringComparison.Ordinal))
            {
                throw new JsonException($"Only local references are supported in a data model schema: {referencePath}");
            }

            var target = root;
            foreach (var segment in referencePath[2..].Split('/'))
            {
                var name = segment.Replace("~1", "/").Replace("~0", "~");
                if (!target.TryGetProperty(name, out target))
                {
                    throw new JsonException($"Reference {referencePath} not found in data model schema");
                }
            }

            return target;
        }

        private void Merge(ReadOnlyNode? other)
        {
            if (other is null)
            {
                return;
            }

            IsReadOnly |= other.IsReadOnly;
            foreach (var (name, child) in other.Properties)
            {
                MergeProperty(name, child);
            }
        }

        private void MergeProperty(string name, ReadOnlyNode child)
        {
            if (Properties.TryGetValue(name, out var existing))
            {
                existing.Merge(child);
            }
            else
            {
                Properties[name] = child;
            }
        }
    }
}

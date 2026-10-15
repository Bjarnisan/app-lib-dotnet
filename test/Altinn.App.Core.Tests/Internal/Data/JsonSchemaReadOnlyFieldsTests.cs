using System.Text.Json;
using System.Text.Json.Serialization;
using Altinn.App.Core.Configuration;
using Altinn.App.Core.Features.Auth;
using Altinn.App.Core.Internal.App;
using Altinn.App.Core.Internal.Data;
using Altinn.Platform.Storage.Interface.Models;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Moq;

namespace Altinn.App.Core.Tests.Internal.Data;

public class JsonSchemaReadOnlyFieldsTests
{
    private const string Schema = """
        {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "type": "object",
            "properties": {
                "submitter": { "$ref": "#/$defs/Person" },
                "organisation": { "$ref": "#/$defs/Organisation", "readOnly": true },
                "message": { "type": "string" },
                "rows": { "type": "array", "items": { "$ref": "#/$defs/Row" } }
            },
            "$defs": {
                "Person": {
                    "properties": {
                        "name": { "type": "string", "readOnly": true },
                        "nationalIdentityNumber": { "type": "string", "readOnly": true },
                        "email": { "type": "string" }
                    }
                },
                "Organisation": {
                    "properties": {
                        "number": { "type": "string" },
                        "name": { "type": "string" }
                    }
                },
                "Row": {
                    "properties": {
                        "amount": { "type": "string", "readOnly": true }
                    }
                }
            }
        }
        """;

    private static readonly DataType _dataType = new() { Id = "model" };

    private readonly Mock<IAppResources> _appResources = new(MockBehavior.Strict);
    private readonly Mock<IAuthenticationContext> _authenticationContext = new(MockBehavior.Strict);
    private readonly AppSettings _appSettings = new() { EnforceJsonSchemaReadOnly = true };
    private readonly FakeLogger<JsonSchemaReadOnlyFields> _logger = new();

    public JsonSchemaReadOnlyFieldsTests()
    {
        _appResources.Setup(r => r.GetModelJsonSchema(_dataType.Id)).Returns(Schema);
        _authenticationContext.Setup(a => a.Current).Returns(TestAuthentication.GetUserAuthentication());
    }

    private JsonSchemaReadOnlyFields CreateReadOnlyFields() =>
        new(_appResources.Object, _authenticationContext.Object, Options.Create(_appSettings), _logger);

    private static Model CreateStoredModel() =>
        new()
        {
            Submitter = new Person
            {
                Name = "Ola Nordmann",
                NationalIdentityNumber = "01039012345",
                Email = "ola@example.com",
            },
            Organisation = new Organisation { Number = "310000001", Name = "Testbedrift AS" },
            Message = "Hello",
            Rows = [new Row { AltinnRowId = Guid.NewGuid(), Amount = "100" }],
        };

    private static Model Copy(Model model) =>
        JsonSerializer.Deserialize<Model>(JsonSerializer.Serialize(model)) ?? throw new InvalidOperationException();

    [Fact]
    public void RestoreReadOnlyFields_KeepsStoredReadOnlyFieldsAndAcceptsOtherChanges()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";
        fromClient.Submitter.NationalIdentityNumber = "02039012345";
        fromClient.Submitter.Email = "kari@example.com";
        fromClient.Message = "Changed";

        var result = (Model)CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Equal("Ola Nordmann", result.Submitter!.Name);
        Assert.Equal("01039012345", result.Submitter.NationalIdentityNumber);
        Assert.Equal("kari@example.com", result.Submitter.Email);
        Assert.Equal("Changed", result.Message);
        Assert.Contains(
            _logger.Collector.GetSnapshot(),
            log => log.Message.Contains("submitter.name, submitter.nationalIdentityNumber")
        );
    }

    [Fact]
    public void RestoreReadOnlyFields_KeepsStoredObjectWhenTheReferenceIsReadOnly()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Organisation!.Name = "Falsk AS";

        var result = (Model)CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Equal("Testbedrift AS", result.Organisation!.Name);
        Assert.Equal("310000001", result.Organisation.Number);
    }

    [Fact]
    public void RestoreReadOnlyFields_KeepsReadOnlyFieldsWhenTheClientRemovesTheParent()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter = null;
        fromClient.Organisation = null;

        var result = (Model)CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Equal("Ola Nordmann", result.Submitter!.Name);
        Assert.Equal("01039012345", result.Submitter.NationalIdentityNumber);
        Assert.Null(result.Submitter.Email);
        Assert.Equal("Testbedrift AS", result.Organisation!.Name);
    }

    [Fact]
    public void RestoreReadOnlyFields_RemovesReadOnlyFieldsTheClientAdds()
    {
        var stored = new Model { Message = "Hello" };
        var fromClient = Copy(stored);
        fromClient.Submitter = new Person { Name = "Kari Nordmann", Email = "kari@example.com" };

        var result = (Model)CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Null(result.Submitter!.Name);
        Assert.Equal("kari@example.com", result.Submitter.Email);
    }

    [Fact]
    public void RestoreReadOnlyFields_ReturnsTheSameObjectWhenNoReadOnlyFieldChanged()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Message = "Changed";

        var result = CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), log => log.Message.StartsWith("Kept the stored value"));
    }

    [Fact]
    public void RestoreReadOnlyFields_DoesNotSupportReadOnlyFieldsInsideLists()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Rows![0].Amount = "200";

        var result = (Model)CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Equal("200", result.Rows![0].Amount);
        Assert.Contains(
            _logger.Collector.GetSnapshot(),
            log => log.Message.Contains("not supported") && log.Message.Contains("rows[]")
        );
    }

    [Fact]
    public void RestoreReadOnlyFields_DoesNothingWhenTheSettingIsOff()
    {
        _appSettings.EnforceJsonSchemaReadOnly = false;
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";

        var result = CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
        _appResources.Verify(r => r.GetModelJsonSchema(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RestoreReadOnlyFields_LetsTheServiceOwnerChangeReadOnlyFields()
    {
        _authenticationContext.Setup(a => a.Current).Returns(TestAuthentication.GetServiceOwnerAuthentication());
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";

        var result = CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
    }

    [Fact]
    public void RestoreReadOnlyFields_DoesNothingWhenTheDataTypeHasNoJsonSchema()
    {
        _appResources.Setup(r => r.GetModelJsonSchema(_dataType.Id)).Throws<FileNotFoundException>();
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";

        var result = CreateReadOnlyFields().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
        var log = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Contains("No JSON schema found for data type model", log.Message);
    }

    [Fact]
    public void FromSchema_FindsReadOnlyFieldsThroughCombinationsAndRecursiveReferences()
    {
        using var schema = JsonDocument.Parse(
            """
            {
                "properties": {
                    "person": { "$ref": "#/$defs/Person" }
                },
                "$defs": {
                    "Person": {
                        "allOf": [{ "$ref": "#/$defs/Named" }],
                        "properties": {
                            "guardian": { "$ref": "#/$defs/Person" }
                        }
                    },
                    "Named": {
                        "properties": {
                            "name": { "type": "string", "readOnly": true },
                            "nickname": { "type": "string" }
                        }
                    }
                }
            }
            """
        );
        List<string> fieldsInLists = [];

        var node = JsonSchemaReadOnlyFields.ReadOnlyNode.FromSchema(schema.RootElement, fieldsInLists);

        // The recursion stops at the reference that is already being followed, so guardian is left out
        Assert.NotNull(node);
        var person = Assert.Single(node.Properties).Value;
        var name = Assert.Single(person.Properties).Value;
        Assert.True(name.IsReadOnly);
        Assert.Empty(fieldsInLists);
    }

    [Fact]
    public void FromSchema_ReturnsNullWhenNothingIsReadOnly()
    {
        using var schema = JsonDocument.Parse("""{ "properties": { "name": { "type": "string" } } }""");

        var node = JsonSchemaReadOnlyFields.ReadOnlyNode.FromSchema(schema.RootElement, []);

        Assert.Null(node);
    }

    public class Model
    {
        [JsonPropertyName("submitter")]
        public Person? Submitter { get; set; }

        [JsonPropertyName("organisation")]
        public Organisation? Organisation { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("rows")]
        public List<Row>? Rows { get; set; }
    }

    public class Person
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("nationalIdentityNumber")]
        public string? NationalIdentityNumber { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    public class Organisation
    {
        [JsonPropertyName("number")]
        public string? Number { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    public class Row
    {
        [JsonPropertyName("altinnRowId")]
        public Guid AltinnRowId { get; set; }

        [JsonPropertyName("amount")]
        public string? Amount { get; set; }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Altinn.App.Core.Configuration;
using Altinn.App.Core.Features.Auth;
using Altinn.App.Core.Features.DataProcessing;
using Altinn.App.Core.Internal.App;
using Altinn.Platform.Storage.Interface.Models;
using Microsoft.Extensions.Logging.Testing;
using Moq;

namespace Altinn.App.Core.Tests.Features.DataProcessing;

public class JsonSchemaReadOnlyEnforcerTests
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
    private readonly FakeLogger<JsonSchemaReadOnlyEnforcer> _logger = new();

    public JsonSchemaReadOnlyEnforcerTests()
    {
        _appResources.Setup(r => r.GetModelJsonSchema(_dataType.Id)).Returns(Schema);
        _authenticationContext.Setup(a => a.Current).Returns(TestAuthentication.GetUserAuthentication());
    }

    private JsonSchemaReadOnlyEnforcer CreateEnforcer(TelemetrySink? telemetry = null) =>
        new(
            _appResources.Object,
            _authenticationContext.Object,
            Microsoft.Extensions.Options.Options.Create(_appSettings),
            _logger,
            telemetry?.Object
        );

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
    public void RestoreReadOnlyFields_RestoresChangedReadOnlyFields_AndKeepsOtherChanges()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";
        fromClient.Submitter.NationalIdentityNumber = "02039012345";
        fromClient.Submitter.Email = "kari@example.com";
        fromClient.Message = "Changed";

        var result = (Model)CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

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
    public void RestoreReadOnlyFields_RestoresWholeObject_WhenObjectIsReadOnly()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Organisation!.Name = "Falsk AS";

        var result = (Model)CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Equal("Testbedrift AS", result.Organisation!.Name);
        Assert.Equal("310000001", result.Organisation.Number);
    }

    [Fact]
    public void RestoreReadOnlyFields_RestoresReadOnlyFields_WhenClientRemovesParentObject()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter = null;
        fromClient.Organisation = null;

        var result = (Model)CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Equal("Ola Nordmann", result.Submitter!.Name);
        Assert.Equal("01039012345", result.Submitter.NationalIdentityNumber);
        Assert.Null(result.Submitter.Email);
        Assert.Equal("Testbedrift AS", result.Organisation!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreReadOnlyFields_ClearsReadOnlyFields_WhenNoValueWasStored(bool submitterIsStored)
    {
        var stored = new Model { Submitter = submitterIsStored ? new Person() : null, Message = "Hello" };
        var fromClient = Copy(stored);
        fromClient.Submitter = new Person { Name = "Kari Nordmann", Email = "kari@example.com" };

        var result = (Model)CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Null(result.Submitter!.Name);
        Assert.Equal("kari@example.com", result.Submitter.Email);
    }

    [Fact]
    public void RestoreReadOnlyFields_ReturnsSameModel_WhenNoReadOnlyFieldChanged()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Message = "Changed";

        var result = CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), log => log.Message.StartsWith("Kept the stored value"));
    }

    [Fact]
    public void RestoreReadOnlyFields_KeepsChangesInsideLists_AndLogsWarning()
    {
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Rows![0].Amount = "200";

        var result = (Model)CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Equal("200", result.Rows![0].Amount);
        Assert.Contains(
            _logger.Collector.GetSnapshot(),
            log => log.Message.Contains("not supported") && log.Message.Contains("rows[]")
        );
    }

    [Theory]
    [InlineData("""{ "type": "array", "readOnly": true, "items": { "$ref": "#/$defs/Row" } }""")]
    [InlineData("""{ "type": "array", "items": { "$ref": "#/$defs/Row", "readOnly": true } }""")]
    public void RestoreReadOnlyFields_RestoresWholeList_WhenListOrItsItemsAreReadOnly(string rowsSchema)
    {
        _appResources
            .Setup(r => r.GetModelJsonSchema(_dataType.Id))
            .Returns(
                $$"""
                {
                    "properties": { "rows": {{rowsSchema}} },
                    "$defs": { "Row": { "properties": { "amount": { "type": "string" } } } }
                }
                """
            );
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Rows![0].Amount = "200";
        fromClient.Rows.Add(new Row { AltinnRowId = Guid.NewGuid(), Amount = "300" });

        var result = (Model)CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        var row = Assert.Single(result.Rows!);
        Assert.Equal("100", row.Amount);
        Assert.DoesNotContain(_logger.Collector.GetSnapshot(), log => log.Message.Contains("not supported"));
    }

    [Fact]
    public void RestoreReadOnlyFields_StartsActivity_WhenReadOnlyFieldsAreChecked()
    {
        using var telemetry = new TelemetrySink();
        var stored = CreateStoredModel();

        CreateEnforcer(telemetry).RestoreReadOnlyFields(_dataType, stored, Copy(stored));

        Assert.Contains(
            telemetry.CapturedActivities,
            activity => activity.OperationName == "JsonSchemaReadOnlyEnforcer.RestoreReadOnlyFields"
        );
    }

    [Fact]
    public void RestoreReadOnlyFields_ReturnsSameModel_WhenSettingIsOff()
    {
        _appSettings.EnforceJsonSchemaReadOnly = false;
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";

        var result = CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
        _appResources.Verify(r => r.GetModelJsonSchema(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void RestoreReadOnlyFields_ReturnsSameModel_WhenServiceOwnerChangesReadOnlyFields()
    {
        _authenticationContext.Setup(a => a.Current).Returns(TestAuthentication.GetServiceOwnerAuthentication());
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";

        var result = CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
    }

    [Fact]
    public void RestoreReadOnlyFields_ReturnsSameModel_WhenDataTypeHasNoJsonSchema()
    {
        _appResources.Setup(r => r.GetModelJsonSchema(_dataType.Id)).Throws<FileNotFoundException>();
        var stored = CreateStoredModel();
        var fromClient = Copy(stored);
        fromClient.Submitter!.Name = "Kari Nordmann";

        var result = CreateEnforcer().RestoreReadOnlyFields(_dataType, stored, fromClient);

        Assert.Same(fromClient, result);
        var log = Assert.Single(_logger.Collector.GetSnapshot());
        Assert.Contains("No JSON schema found for data type model", log.Message);
    }

    [Fact]
    public void FromSchema_FindsReadOnlyFields_ThroughCombinationsAndRecursiveReferences()
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
                            "guardian": { "$ref": "#/$defs/Person" },
                            "address": { "$ref": "#/$defs/Address" }
                        }
                    },
                    "Address": {
                        "properties": {
                            "street": { "type": "string" }
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
        List<string> listsWithReadOnlyFields = [];

        var node = JsonSchemaReadOnlyEnforcer.ReadOnlyNode.FromSchema(schema.RootElement, listsWithReadOnlyFields);

        // The recursion stops at the reference that is already being followed, so guardian is left out,
        // and address has no read-only fields
        Assert.NotNull(node);
        var person = Assert.Single(node.Properties).Value;
        var name = Assert.Single(person.Properties).Value;
        Assert.True(name.IsReadOnly);
        Assert.Empty(listsWithReadOnlyFields);
    }

    [Fact]
    public void FromSchema_FindsReadOnlyFields_ThroughReferenceToRoot()
    {
        using var schema = JsonDocument.Parse(
            """{ "properties": { "name": { "readOnly": true }, "parent": { "$ref": "#" } } }"""
        );

        var node = JsonSchemaReadOnlyEnforcer.ReadOnlyNode.FromSchema(schema.RootElement, []);

        Assert.NotNull(node);
        Assert.True(node.Properties["name"].IsReadOnly);
        Assert.True(node.Properties["parent"].Properties["name"].IsReadOnly);
    }

    [Fact]
    public void FromSchema_MergesReadOnlyProperties_FromEachSubschema()
    {
        using var schema = JsonDocument.Parse(
            """
            {
                "allOf": [
                    { "properties": { "contact": { "properties": { "email": { "readOnly": true } } } } },
                    { "properties": { "contact": { "properties": { "phone": { "readOnly": true } } } } }
                ]
            }
            """
        );

        var node = JsonSchemaReadOnlyEnforcer.ReadOnlyNode.FromSchema(schema.RootElement, []);

        Assert.NotNull(node);
        var contact = Assert.Single(node.Properties).Value;
        Assert.Equal(new[] { "email", "phone" }, contact.Properties.Keys.Order());
    }

    [Theory]
    [InlineData("other.schema.json#/$defs/Person")]
    [InlineData("#/$defs/Missing")]
    public void FromSchema_ThrowsJsonException_WhenReferenceCannotBeResolved(string reference)
    {
        using var schema = JsonDocument.Parse(
            $$"""{ "properties": { "person": { "$ref": "{{reference}}" } }, "$defs": {} }"""
        );

        Assert.Throws<JsonException>(() => JsonSchemaReadOnlyEnforcer.ReadOnlyNode.FromSchema(schema.RootElement, []));
    }

    [Fact]
    public void FromSchema_ReturnsNull_WhenNothingIsReadOnly()
    {
        using var schema = JsonDocument.Parse(
            """
            {
                "properties": {
                    "name": { "type": "string" },
                    "anything": true,
                    "tags": { "type": "array", "items": { "type": "string" } }
                }
            }
            """
        );

        var node = JsonSchemaReadOnlyEnforcer.ReadOnlyNode.FromSchema(schema.RootElement, []);

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

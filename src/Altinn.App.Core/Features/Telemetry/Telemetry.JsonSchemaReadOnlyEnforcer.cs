using System.Diagnostics;
using static Altinn.App.Core.Features.Telemetry.JsonSchemaReadOnlyEnforcer;

namespace Altinn.App.Core.Features;

partial class Telemetry
{
    internal Activity? StartRestoreReadOnlyFieldsActivity() =>
        ActivitySource.StartActivity($"{Prefix}.RestoreReadOnlyFields");

    internal static class JsonSchemaReadOnlyEnforcer
    {
        internal const string Prefix = "JsonSchemaReadOnlyEnforcer";
    }
}

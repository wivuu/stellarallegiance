using Aspire.Hosting.Publishing;

namespace StellarAllegiance.AppHost.Hosting;

// "Use the configured value if there is one, else this constant" - so optional settings never trip the
// dashboard's Unresolved-parameters prompt, while .env / user secrets / Parameters__ still override.
public sealed class ConstantParameterDefault(string value) : ParameterDefault
{
    public override string GetDefaultValue() => value;

    public override void WriteToManifest(ManifestPublishingContext context)
    {
        // Local-dev AppHost: nothing is published, so the manifest shape is irrelevant.
    }
}

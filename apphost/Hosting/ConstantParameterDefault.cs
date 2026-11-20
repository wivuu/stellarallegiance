using Aspire.Hosting.Publishing;

namespace StellarAllegiance.AppHost.Hosting;

// "Use the configured value if there is one, else this constant": .env / user secrets / Parameters__ still
// override. Paired with AsOptional() in AppParameters.Param, which is what keeps these off the dashboard's
// unresolved-parameters prompt.
public sealed class ConstantParameterDefault(string value) : ParameterDefault
{
    public override string GetDefaultValue() => value;

    public override void WriteToManifest(ManifestPublishingContext context)
    {
        // Local-dev AppHost: nothing is published, so the manifest shape is irrelevant.
    }
}

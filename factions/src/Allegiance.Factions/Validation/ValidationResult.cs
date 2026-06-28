namespace Allegiance.Factions.Validation;

/// <summary>The outcome of validating a <see cref="Model.Core"/>: a list of errors and warnings.</summary>
public sealed class ValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>True when there are no errors (warnings are allowed).</summary>
    public bool IsValid => Errors.Count == 0;

    public void Error(string message) => Errors.Add(message);
    public void Warn(string message) => Warnings.Add(message);
}

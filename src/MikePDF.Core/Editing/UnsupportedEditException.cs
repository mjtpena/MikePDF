namespace MikePDF.Core.Editing;

/// <summary>A pending command the save pipeline of this build cannot write (an annotation command no stage 4 generator exists for, or fields that do not fit).</summary>
public sealed class UnsupportedEditException(string command, string reason) : InvalidOperationException($"{command}: {reason}")
{
    public string Command { get; } = command;
}

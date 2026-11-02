namespace MikePDF.Core;

/// <summary>
/// The one gate for the test seams: environment variables that let a journey steer the app (a throwaway policy key, the restore mode, a fake dirty flag, a Jump List dump). Every seam is
/// read through here and none is read when the app runs from a Store-signed package, so a shipped build cannot be steered by its environment. The list below is the whole list; the docs
/// (docs/features/tabs-windows-session.md, "Test seams") name the same ones and a test keeps the two equal.
/// </summary>
public static class TestSeamGate
{
    public const string PolicyKey = "MIKEPDF_TEST_POLICY_KEY";
    public const string Restore = "MIKEPDF_TEST_RESTORE";
    public const string FakeDirty = "MIKEPDF_TEST_FAKE_DIRTY";
    public const string JumpListDump = "MIKEPDF_TEST_JUMPLIST_DUMP";

    public static IReadOnlyList<string> All { get; } = [PolicyKey, Restore, FakeDirty, JumpListDump];

    /// <summary>
    /// The value of a seam, or null when it is not set, is empty, is not one of <see cref="All"/>, or the package is Store-signed.
    /// <paramref name="environment"/> reads an environment variable; <paramref name="storeSigned"/> is whether the running package's signature kind is Store.
    /// </summary>
    public static string? Read(string name, Func<string, string?> environment, bool storeSigned)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (storeSigned || !All.Contains(name, StringComparer.Ordinal))
        {
            return null;
        }

        var value = environment(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

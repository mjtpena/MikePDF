using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>Enterprise policies: reading (SET-07, SET-30), resolution (SET-02, SET-03, user scope), the report (SET-10) and the docs lint (SET-25).</summary>
public sealed class PolicyTests
{
    private static readonly string[] HttpsAndMail = ["https", "mailto"];

    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

    private static PolicyEntry Resolve(RawPolicyValue? machine, RawPolicyValue? user, string name)
    {
        var source = new FixedPolicySource();
        if (machine is not null)
        {
            source.Set(PolicyScope.Machine, machine);
        }

        if (user is not null)
        {
            source.Set(PolicyScope.User, user);
        }

        return new PolicyProvider(source).Current[name];
    }

    // ---- SET-07: reading values ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void SET07_TheParserReadsDwordStringAndMultiStringBytesAndTrimsNothingItShouldNot()
    {
        var dword = PolicyValueParser.Parse("X", 4, BitConverter.GetBytes(2));
        Assert.Equal((RawPolicyKind.Dword, 2), (dword.Kind, dword.Dword));
        Assert.Equal(RawPolicyKind.Other, PolicyValueParser.Parse("X", 4, new byte[3]).Kind); // a DWORD of the wrong size
        Assert.Equal(RawPolicyKind.Other, PolicyValueParser.Parse("X", 3, new byte[] { 1, 2 }).Kind); // binary
        Assert.Equal(RawPolicyKind.Other, PolicyValueParser.Parse("X", 2, Utf16("%PATH%\0")).Kind); // an expandable string is not a string for us
        Assert.Equal(RawPolicyKind.Other, PolicyValueParser.Parse("X", 11, new byte[8]).Kind); // a QWORD

        var text = PolicyValueParser.Parse("X", 1, Utf16("  Dr Who \0"));
        Assert.Equal((RawPolicyKind.Text, "  Dr Who "), (text.Kind, text.Text)); // trimming is the policy rules' job
        Assert.Equal(string.Empty, PolicyValueParser.Parse("X", 1, []).Text);

        var multi = PolicyValueParser.Parse("X", 7, Utf16("https\0mailto\0\0"));
        Assert.Equal(["https", "mailto"], multi.Lines);
        Assert.Equal(["a"], PolicyValueParser.Parse("X", 7, Utf16("a\0")).Lines); // no final empty string
        Assert.Equal(["a"], PolicyValueParser.Parse("X", 7, Utf16("a")).Lines);
        Assert.Empty(PolicyValueParser.Parse("X", 7, Utf16("\0")).Lines!);
        Assert.Equal(["a"], PolicyValueParser.Parse("X", 7, [.. Utf16("a\0\0"), 0x41]).Lines); // an odd trailing byte is dropped
    }

    [Fact]
    public void SET07_ANameIsMatchedWithoutRegardToCaseAndAStringIsTrimmed()
    {
        var source = new FixedPolicySource();
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("javascriptENABLED", 0));
        source.Set(PolicyScope.Machine, SettingsFixture.Text("annotationauthorname", "  Dir Name \t"));
        var snapshot = new PolicyProvider(source).Current;
        Assert.True(snapshot["JavaScriptEnabled"].IsConfigured);
        Assert.Equal("Dir Name", snapshot["AnnotationAuthorName"].Text);
        Assert.Equal(snapshot["JAVASCRIPTENABLED"], snapshot["JavaScriptEnabled"]);
    }

    [Fact]
    public void SET07_ListsAreCappedInEntriesAndLengthAndEachEntryIsChecked()
    {
        PolicyEntry Schemes(params string[] lines) => Resolve(SettingsFixture.Lines("AllowedLinkSchemes", lines), null, "AllowedLinkSchemes");
        Assert.Equal(["https", "http", "mailto"], Schemes("HTTPS", " http ", "mailto").Lines); // lower case and trimmed
        Assert.Equal(["https"], Schemes("https", "file").Lines); // "file" is ignored, never allowed
        Assert.False(Schemes("https", "not a scheme").IsConfigured);
        Assert.False(Schemes("1http").IsConfigured);
        Assert.False(Schemes([.. Enumerable.Range(0, 17).Select(i => "s" + i)]).IsConfigured); // more than 16
        Assert.True(Schemes([.. Enumerable.Range(0, 16).Select(i => "s" + i)]).IsConfigured);
        Assert.False(Schemes(new string('a', 33)).IsConfigured);
        Assert.False(Schemes(" ").IsConfigured);

        PolicyEntry Endpoints(params string[] lines) => Resolve(SettingsFixture.Lines("AiAllowedEndpoints", lines), null, "AiAllowedEndpoints");
        Assert.True(Endpoints("https://ai.example.com/v1").IsConfigured);
        Assert.False(Endpoints("http://ai.example.com").IsConfigured);
        Assert.False(Endpoints("ai.example.com").IsConfigured);
        Assert.False(Endpoints("https://" + new string('a', 1030) + ".example").IsConfigured);

        Assert.False(Resolve(SettingsFixture.Text("AnnotationAuthorName", new string('n', 129)), null, "AnnotationAuthorName").IsConfigured);
        Assert.True(Resolve(SettingsFixture.Text("AnnotationAuthorName", new string('n', 128)), null, "AnnotationAuthorName").IsConfigured);
    }

    [Fact]
    public void SET07_TheRegistrySourceReadsEveryKindOfValueFromAThrowawayUserKeyAndRefusesTheOversizeOnes()
    {
        var root = @"Software\MikePDF.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var machine = Registry.CurrentUser.CreateSubKey(root + @"\Machine"))
            using (var user = Registry.CurrentUser.CreateSubKey(root + @"\User"))
            {
                machine.SetValue("javascriptenabled", 0, RegistryValueKind.DWord); // wrong case on purpose
                machine.SetValue("AnnotationAuthorName", "  Reg Name  ", RegistryValueKind.String);
                machine.SetValue("AllowedLinkSchemes", HttpsAndMail, RegistryValueKind.MultiString);
                machine.SetValue("ExternalLinks", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
                machine.SetValue("SaveMode", "1", RegistryValueKind.String); // a string where a DWORD belongs
                machine.SetValue("AiAllowedEndpoints", new string('x', 200_000).Select(_ => "a").ToArray(), RegistryValueKind.MultiString); // 200,000 entries
                machine.SetValue("UpdateChannel", new string('y', 1_000_000), RegistryValueKind.String); // a 2 MB string in UTF-16
                machine.SetValue("PrintingEnabled", 1, RegistryValueKind.QWord);
                machine.SetValue("NotOurs", 5, RegistryValueKind.DWord);
                user.SetValue("ExternalLinks", 0, RegistryValueKind.DWord);
            }

            var source = new RegistryPolicySource(Registry.CurrentUser, root + @"\Machine", Registry.CurrentUser, root + @"\User");
            var watch = Stopwatch.StartNew();
            var snapshot = new PolicyProvider(source).Current;
            watch.Stop();

            Assert.Equal(0, snapshot["JavaScriptEnabled"].Value);
            Assert.True(snapshot["JavaScriptEnabled"].IsConfigured);
            Assert.Equal("Reg Name", snapshot["AnnotationAuthorName"].Text);
            Assert.Equal(["https", "mailto"], snapshot["AllowedLinkSchemes"].Lines);
            Assert.Equal(ObservationOutcome.InvalidClosed, snapshot["ExternalLinks"].Observations[0].Outcome); // binary, fail closed
            Assert.Equal(0, snapshot["ExternalLinks"].Value);
            Assert.False(snapshot["SaveMode"].IsConfigured);
            Assert.Equal(ObservationOutcome.Invalid, snapshot["SaveMode"].Observations.Single().Outcome);
            Assert.True(snapshot["AiAllowedEndpoints"].Observations.Single().Raw.TooLarge); // never read, so never allocated
            Assert.True(snapshot["UpdateChannel"].Observations.Single().Raw.TooLarge);
            Assert.False(snapshot["PrintingEnabled"].IsConfigured);
            Assert.True(watch.ElapsedMilliseconds < 1000, "reading hostile values took " + watch.ElapsedMilliseconds + " ms");

            using (var machine = Registry.CurrentUser.OpenSubKey(root + @"\Machine", writable: true)!)
            {
                machine.SetValue("JavaScriptEnabled", 1, RegistryValueKind.DWord);
            }

            var provider = new PolicyProvider(source);
            Assert.Equal(1, provider.Current["JavaScriptEnabled"].Value);
            Assert.False(provider.Refresh()); // nothing changed since: no event
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\MikePDF.Tests");
            if (parent is { SubKeyCount: 0 })
            {
                parent.Dispose();
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\MikePDF.Tests", throwOnMissingSubKey: false);
            }
        }
    }

    [Fact]
    public void SET07_AMissingKeyIsNotConfiguredAndNotAFailure()
    {
        var root = @"Software\MikePDF.Tests\missing-" + Guid.NewGuid().ToString("N");
        var source = new RegistryPolicySource(Registry.CurrentUser, root + @"\M", Registry.CurrentUser, root + @"\U");
        var snapshot = new PolicyProvider(source).Current;
        Assert.All(snapshot.Entries, e => Assert.Equal(PolicyState.NotConfigured, e.State));
        Assert.False(snapshot.AnyPresent);
        Assert.Equal(0, snapshot.InEffectCount);
        Assert.Null(Registry.CurrentUser.OpenSubKey(root)); // reading never created it
    }

    // ---- resolution --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMachineValueBeatsTheUserValueAndTheUserCanOnlyTighten()
    {
        // ExternalLinks: 0 block, 1 show the address, 2 confirm. A smaller number is stricter.
        var machineOnly = Resolve(SettingsFixture.Dword("ExternalLinks", 1), null, "ExternalLinks");
        Assert.Equal((1, PolicyScope.Machine), (machineOnly.Value, machineOnly.Scope));

        var tighter = Resolve(SettingsFixture.Dword("ExternalLinks", 1), SettingsFixture.Dword("ExternalLinks", 0), "ExternalLinks");
        Assert.Equal((0, PolicyScope.User), (tighter.Value, tighter.Scope));
        Assert.Equal([ObservationOutcome.Overridden, ObservationOutcome.InEffect], tighter.Observations.Select(o => o.Outcome));

        var looser = Resolve(SettingsFixture.Dword("ExternalLinks", 0), SettingsFixture.Dword("ExternalLinks", 2), "ExternalLinks");
        Assert.Equal((0, PolicyScope.Machine), (looser.Value, looser.Scope));
        Assert.Equal([ObservationOutcome.InEffect, ObservationOutcome.NotStricter], looser.Observations.Select(o => o.Outcome));

        var same = Resolve(SettingsFixture.Dword("ExternalLinks", 0), SettingsFixture.Dword("ExternalLinks", 0), "ExternalLinks");
        Assert.Equal(PolicyScope.Machine, same.Scope);
    }

    [Fact]
    public void WithNoMachineValueAUserValueCountsOnlyIfItIsStricterThanTheDefault()
    {
        var tighter = Resolve(null, SettingsFixture.Dword("ExternalLinks", 0), "ExternalLinks");
        Assert.Equal((true, 0, PolicyScope.User), (tighter.IsConfigured, tighter.Value, tighter.Scope));
        var looser = Resolve(null, SettingsFixture.Dword("ExternalLinks", 2), "ExternalLinks"); // the default is 1
        Assert.Equal((false, 1), (looser.IsConfigured, looser.Value));
        Assert.Equal(ObservationOutcome.NotStricter, looser.Observations.Single().Outcome);

        // SaveMode: 1 (always a copy) is the stricter one.
        Assert.True(Resolve(null, SettingsFixture.Dword("SaveMode", 1), "SaveMode").IsConfigured);
        Assert.Equal(1, Resolve(SettingsFixture.Dword("SaveMode", 0), SettingsFixture.Dword("SaveMode", 1), "SaveMode").Value);
        Assert.Equal(1, Resolve(SettingsFixture.Dword("SaveMode", 1), SettingsFixture.Dword("SaveMode", 0), "SaveMode").Value);

        // A limit: the lower one is stricter.
        Assert.Equal(1024, Resolve(SettingsFixture.Dword("EngineMemoryLimitMB", 2048), SettingsFixture.Dword("EngineMemoryLimitMB", 1024), "EngineMemoryLimitMB").Value);
        Assert.Equal(2048, Resolve(SettingsFixture.Dword("EngineMemoryLimitMB", 2048), SettingsFixture.Dword("EngineMemoryLimitMB", 4096), "EngineMemoryLimitMB").Value);
        Assert.Equal(2048, Resolve(null, SettingsFixture.Dword("EngineMemoryLimitMB", 4096), "EngineMemoryLimitMB").Value);

        // AI mode has no default to compare with: any user value tightens "not configured".
        Assert.Equal(1, Resolve(null, SettingsFixture.Dword("AiMode", 1), "AiMode").Value);
    }

    [Fact]
    public void PoliciesThatDoNotTakeUserValuesIgnoreThemAndSayWhy()
    {
        var author = Resolve(null, SettingsFixture.Text("AnnotationAuthorName", "Me"), "AnnotationAuthorName");
        Assert.False(author.IsConfigured);
        Assert.Equal(ObservationOutcome.UserScopeNotAllowed, author.Observations.Single().Outcome);

        var prompt = Resolve(null, SettingsFixture.Dword("DefaultAppPromptEnabled", 0), "DefaultAppPromptEnabled");
        Assert.False(prompt.IsConfigured);
        Assert.Equal(ObservationOutcome.UserScopeNotAllowed, prompt.Observations.Single().Outcome);
        Assert.False(Resolve(null, SettingsFixture.Lines("AllowedLinkSchemes", "https"), "AllowedLinkSchemes").IsConfigured);
    }

    [Fact]
    public void ARefreshRaisesChangedOnlyWhenWhatIsInEffectChanges()
    {
        var source = new FixedPolicySource();
        var provider = new PolicyProvider(source);
        var changes = 0;
        provider.Changed += () => changes++;
        Assert.False(provider.Refresh());
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("PrintingEnabled", 0));
        Assert.True(provider.Refresh());
        Assert.False(provider.Refresh());
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("PrintingEnabled", 1));
        Assert.True(provider.Refresh());
        source.Failing.Add(PolicyScope.Machine);
        Assert.True(provider.Refresh()); // a hive that became unreadable is a change too
        Assert.Equal(3, changes);
        Assert.True(provider.Current.PrintingAllowed);
    }

    [Fact]
    public void AProviderWhoseSourceThrowsTreatsItAsUnreadableAndKeepsGoing()
    {
        var provider = new PolicyProvider(new ThrowingSource());
        Assert.Equal(PolicyState.ReadFailed, provider.Current["JavaScriptEnabled"].State);
        Assert.Equal(0, provider.Current["SettingsImportExportEnabled"].Value);
        Assert.False(provider.Current.ImportExportAllowed); // fail closed
        Assert.True(provider.Current.PrintingAllowed); // PrintingEnabled is not fail closed
    }

    private sealed class ThrowingSource : IPolicySource
    {
        public PolicyRead Read(PolicyScope scope, IReadOnlyCollection<string> names) => throw new InvalidOperationException("registry gone");
    }

    // ---- SET-10: the report ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void SET10_TheReportForScenarioNineNamesJavaScriptAiAndTelemetryWithEffectAndSourceAndFlagsABadValue()
    {
        var source = new FixedPolicySource();
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("JavaScriptEnabled", 0));
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("AiMode", 1));
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("TelemetryEnabled", 0));
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("PrintingEnabled", 9)); // the deliberately invalid fourth
        var report = PolicyReport.Build(new PolicyProvider(source).Current, ResourceText.Get);

        Assert.True(report.AnyPresent);
        Assert.Equal(3, report.InEffectCount);
        Assert.Equal(4, report.Lines.Count);
        string Line(string name) => report.Lines.Single(l => l.PolicyName == name).Text(ResourceText.Get);

        Assert.Equal("JavaScript in documents: set to 0. JavaScript: off (Registry policy, machine)", Line("JavaScriptEnabled"));
        Assert.Equal("AI features: set to 1. AI: local only. Reserved for a future version (Registry policy, machine)", Line("AiMode"));
        Assert.Equal("Telemetry: set to 0. Telemetry: off (this build has none) (Registry policy, machine)", Line("TelemetryEnabled"));
        Assert.Equal("Printing: set to 9. Ignored: not a valid value (Registry policy, machine)", Line("PrintingEnabled"));
        Assert.True(report.Lines.Single(l => l.PolicyName == "AiMode").InEffect);
        Assert.False(report.Lines.Single(l => l.PolicyName == "PrintingEnabled").InEffect);
    }

    [Fact]
    public void SET10_TheReportSaysReservedNotInBuildIgnoredAndUserScopeInWords()
    {
        var source = new FixedPolicySource();
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("JavaScriptEnabled", 1));
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("AllowWeakerSandbox", 0));
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("UpdateChannel", 0));
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("ExternalLinks", 12));
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("EngineMemoryLimitMB", 1024));
        source.Set(PolicyScope.User, SettingsFixture.Dword("ExternalLinks", 2));
        source.Set(PolicyScope.User, SettingsFixture.Text("AnnotationAuthorName", "Me"));
        var report = PolicyReport.Build(new PolicyProvider(source).Current, ResourceText.Get);
        string Line(string name, int index = 0) => report.Lines.Where(l => l.PolicyName == name).ElementAt(index).Text(ResourceText.Get);

        Assert.Contains("JavaScript: not available in this build", Line("JavaScriptEnabled"), StringComparison.Ordinal);
        Assert.Contains("Ignored: this build does not use it", Line("AllowWeakerSandbox"), StringComparison.Ordinal);
        Assert.Contains("Ignored: this build does not use it", Line("UpdateChannel"), StringComparison.Ordinal);
        Assert.Contains("Not a valid value, so the most restrictive result applies: Links that lead outside the document are blocked", Line("ExternalLinks"), StringComparison.Ordinal);
        Assert.Contains("Each viewer process may use up to 1024 MB", Line("EngineMemoryLimitMB"), StringComparison.Ordinal);
        Assert.Contains("(Registry policy, user)", Line("ExternalLinks", 1), StringComparison.Ordinal);
        Assert.Contains("only read from the machine", Line("AnnotationAuthorName"), StringComparison.Ordinal);
    }

    [Fact]
    public void SET10_TheCopiedReportHasTheHeaderAndTheLinesAndNoPathsOrNames()
    {
        var source = new FixedPolicySource();
        source.Set(PolicyScope.Machine, SettingsFixture.Dword("JavaScriptEnabled", 0));
        var report = PolicyReport.Build(new PolicyProvider(source).Current, ResourceText.Get);
        var text = report.ToPlainText(["MikePDF 1.0.0.0, x64", "Windows 11"], ResourceText.Get);
        Assert.StartsWith("MikePDF 1.0.0.0, x64", text, StringComparison.Ordinal);
        Assert.Contains("Managed by your organisation", text, StringComparison.Ordinal);
        Assert.Contains("JavaScript in documents: set to 0. JavaScript: off (Registry policy, machine)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@":\", text, StringComparison.Ordinal);

        var none = PolicyReport.Build(PolicyProvider.None().Current, ResourceText.Get);
        Assert.False(none.AnyPresent);
        Assert.Empty(none.Lines);
        Assert.DoesNotContain("Managed by your organisation", none.ToPlainText(["MikePDF"], ResourceText.Get), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadablePolicyHiveIsReportedAndFailClosedPoliciesShowTheirRestrictiveResult()
    {
        var source = new FixedPolicySource();
        source.Failing.Add(PolicyScope.Machine);
        var report = PolicyReport.Build(new PolicyProvider(source).Current, ResourceText.Get);
        Assert.True(report.AnyPresent);
        var js = report.Lines.Single(l => l.PolicyName == "JavaScriptEnabled");
        Assert.Contains("The policy couldn't be read. JavaScript: off", js.Effect, StringComparison.Ordinal);
        Assert.True(js.InEffect);
        var printing = report.Lines.Single(l => l.PolicyName == "PrintingEnabled");
        Assert.False(printing.InEffect);
    }

    // ---- every policy has strings, and SET-25 -----------------------------------------------------------------------------------------

    [Fact]
    public void EveryPolicyHasAFriendlyNameAndWordsForEachOfItsValues()
    {
        foreach (var policy in PolicyCatalog.All)
        {
            Assert.True(ResourceText.Has(policy.NameKey), policy.NameKey);
            if (policy.Kind == PolicyKind.Dword && policy.Allowed is { } allowed)
            {
                Assert.All(allowed, v => Assert.True(ResourceText.Has(policy.ValueKey(v)), policy.ValueKey(v)));
            }
            else
            {
                Assert.True(ResourceText.Has(policy.EffectKey), policy.EffectKey);
            }
        }
    }

    [Fact]
    public void SET25_TheAdminDocumentListsExactlyThePoliciesInTheCatalogue()
    {
        var path = Path.Combine(ResourceText.RepoRoot(), "docs", "admin", "policies.md");
        Assert.True(File.Exists(path), "docs/admin/policies.md is missing");
        var documented = File.ReadAllLines(path)
            .Where(l => l.StartsWith("| `", StringComparison.Ordinal))
            .Select(l => l.Split('|')[1].Trim().Trim('`'))
            .ToList();
        Assert.Equal(PolicyCatalog.All.Select(p => p.Name).Order(StringComparer.Ordinal), documented.Order(StringComparer.Ordinal));
        Assert.Equal(documented.Count, documented.Distinct().Count());
        Assert.Contains(PolicyCatalog.RegistryPath, File.ReadAllText(path), StringComparison.Ordinal);
    }
}

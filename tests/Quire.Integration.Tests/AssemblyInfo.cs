using Quire.Integration.Tests.Support;

// One AppContainer profile and one deployed engine directory for the whole run, removed at the end even if tests fail.
[assembly: AssemblyFixture(typeof(SandboxFixture))]

// Timing-sensitive tests (watchdog, deadlines, restore) must not compete with each other for the machine.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

using MikePDF.Perf.Tests;

// One AppContainer profile and one deployed engine for the whole run, removed at the end even if tests fail.
[assembly: AssemblyFixture(typeof(HarnessFixture))]

// The tests that start engines measure time. Running them side by side would measure the test runner.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

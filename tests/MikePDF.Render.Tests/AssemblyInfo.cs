using MikePDF.Render.Tests.Support;

// The AppContainer profile the live tests create is removed at the end, even if tests fail.
[assembly: AssemblyFixture(typeof(ProfileCleanup))]

// Tests that kill engines or race a deadline must not compete with each other for the machine.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

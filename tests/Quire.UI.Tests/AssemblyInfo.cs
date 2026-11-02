// Every journey drives the one Quire on the one desktop (one keyboard, one pointer, one foreground window, and each launch closes any running
// Quire), so no two of them may run at the same time, even in different test classes: the whole assembly is a single collection.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

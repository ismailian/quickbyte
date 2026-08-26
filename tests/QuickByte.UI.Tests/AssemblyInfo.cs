using Xunit;

// StartupRegistration and QueueAgentRegistration read one static key path each,
// and the tests redirect both at a scratch key of their own (ScratchRunKey).
// Two of those running at once would be writing to each other's key -- or, for
// the moment one restores the path, to the user's real one. The whole suite is
// in-memory bar a few registry values, so serialising it costs nothing worth
// having.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

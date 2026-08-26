using Xunit;

// AgentLog is static and writes to one path, and the log is the only place most
// of the agent's decisions are observable — so a test that wants to read one
// points that path at a scratch file of its own. Two doing it at once would read
// each other's lines. The whole suite is in-memory and runs in well under a
// second, so serialising it costs nothing worth having.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

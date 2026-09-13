using Xunit.Sdk;
using Xunit.v3;

// Integration tests talk to one real server. Test classes are separate collections and would
// otherwise run in parallel, opening several connections at once — which the server treats as a
// flood and answers with an IP-level block. Within a class, the shared LiveServerFixture keeps it
// to a single session.
[assembly: Parallelization(Mode = ParallelMode.None)]
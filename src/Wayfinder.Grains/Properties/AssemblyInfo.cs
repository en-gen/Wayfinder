using System.Runtime.CompilerServices;

// ADO #59 - lets Wayfinder.Grains.Tests unit-test Events.ActorStamping (internal - it's a stamping
// helper, not part of the grain-facing public surface) in isolation, without spinning up a
// TestCluster just to exercise CaseRequestContext fallback behavior.
[assembly: InternalsVisibleTo("Wayfinder.Grains.Tests")]

using Xunit;

namespace SendSpin.Tests
{
    /// <summary>
    /// The wire-level tests bind real localhost HttpListeners; run them serially so they don't
    /// race on the fake server's port range (xUnit runs test classes in parallel by default).
    /// </summary>
    [CollectionDefinition(nameof(WireTests), DisableParallelization = true)]
    public sealed class WireTests { }
}
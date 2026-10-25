using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Mcp.Tests;

public sealed class StudioAgentProfileLibraryTests
{
    [Fact]
    public void ImmutableProfileCanBeReloadedMovedAndDeletedWithoutChangingItsIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vaw-agent-profile-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = AgentProfile.Create("summarizer", "1.0.0", "Summarizer", "Summarizes source text.",
                "demo.scripted", "1.0.0", "Return a brief summary.", []);
            var firstLibrary = new StudioAgentProfileLibrary(directory);
            firstLibrary.Add(profile, "Writing/Review");

            var reloadedLibrary = new StudioAgentProfileLibrary(directory);
            var saved = Assert.Single(reloadedLibrary.List());
            Assert.Equal(profile.ContentHash, saved.Profile.ContentHash);
            Assert.Equal("Writing/Review", saved.Folder);
            Assert.Throws<ArgumentException>(() => reloadedLibrary.Add(profile, "Other"));

            Assert.True(reloadedLibrary.Move(profile.Id, profile.Version, "Research"));
            Assert.Equal("Research", Assert.Single(reloadedLibrary.List()).Folder);
            Assert.True(reloadedLibrary.Delete(profile.Id, profile.Version));
            Assert.Empty(reloadedLibrary.List());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

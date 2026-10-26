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
                "demo.scripted", "Return a brief summary.", [], channels: ["research"]);
            var firstLibrary = new StudioAgentProfileLibrary(directory);
            firstLibrary.Add(profile, "Writing/Review");

            var reloadedLibrary = new StudioAgentProfileLibrary(directory);
            var saved = Assert.Single(reloadedLibrary.List());
            Assert.Equal(profile.ContentHash, saved.Profile.ContentHash);
            Assert.Equal(profile.Channels, saved.Profile.Channels);
            Assert.Equal("Writing/Review", saved.Folder);
            Assert.Throws<ArgumentException>(() => reloadedLibrary.Add(profile, "Other"));
            var invalidChannel = AgentProfile.Create("invalid-channel", "1", "Invalid", "Bad channel ID.",
                "demo.scripted", "Instructions.", [], channels: ["Research Channel"]);
            Assert.Throws<ArgumentException>(() => reloadedLibrary.Add(invalidChannel, "Other"));

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

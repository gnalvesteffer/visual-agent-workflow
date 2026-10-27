using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Local profile authoring catalog; workflows embed the exact assigned profile revisions.</summary>
public sealed class StudioAgentProfileLibrary : IAgentProfileLibrary
{
    private readonly object gate = new();
    private readonly string path;
    private List<AgentProfileLibraryItem> items;

    public StudioAgentProfileLibrary(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "agent-profiles.json");
        items = File.Exists(path) ? JsonSerializer.Deserialize<List<AgentProfileLibraryItem>>(File.ReadAllText(path)) ?? [] : [];
        if (items.Any(item => item is null || !IsIntact(item.Profile)))
            throw new InvalidDataException("The agent profile library contains a modified revision.");
    }

    public IReadOnlyList<AgentProfileLibraryItem> List()
    {
        lock (gate) return items.OrderBy(item => item.Folder, StringComparer.Ordinal)
            .ThenBy(item => item.Profile.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Profile.Version, StringComparer.Ordinal).ToArray();
    }

    public void Add(AgentProfile profile, string folder)
    {
        Validate(profile, folder);
        lock (gate)
        {
            if (items.Any(item => item.Profile.Id == profile.Id && item.Profile.Version == profile.Version))
                throw new ArgumentException("This agent profile version already exists. Use a new version for edits.");
            Save([.. items, new(profile, folder)]);
        }
    }

    public bool Move(string id, string version, string folder)
    {
        ValidateFolder(folder);
        lock (gate)
        {
            var index = items.FindIndex(item => item.Profile.Id == id && item.Profile.Version == version);
            if (index < 0) return false;
            var updated = items.ToList();
            updated[index] = updated[index] with { Folder = folder };
            Save(updated);
            return true;
        }
    }

    public bool Delete(string id, string version)
    {
        lock (gate)
        {
            var updated = items.Where(item => item.Profile.Id != id || item.Profile.Version != version).ToList();
            if (updated.Count == items.Count) return false;
            Save(updated);
            return true;
        }
    }

    private void Save(List<AgentProfileLibraryItem> updated)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, path, true);
        items = updated;
    }

    private static bool IsIntact(AgentProfile? profile) => profile is not null &&
        profile.ContentHash == AgentProfile.Create(profile.Id, profile.Version, profile.Name, profile.Description,
            profile.ProviderId, profile.ProviderVersion, profile.Instructions, profile.Tools, profile.Skills,
            profile.ReasoningEffort, profile.MaxOutputTokens, profile.Channels).ContentHash;

    internal static void Validate(AgentProfile profile, string folder)
    {
        if (profile is null || !IsIntact(profile) || string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 64 ||
            string.IsNullOrWhiteSpace(profile.Version) || profile.Version.Length > 64 || profile.Version.Contains('@') ||
            string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 128 ||
            string.IsNullOrWhiteSpace(profile.Description) || profile.Description.Length > 512 ||
            string.IsNullOrWhiteSpace(profile.ProviderId) || profile.ProviderId.Length > 128 ||
            string.IsNullOrWhiteSpace(profile.Instructions) || profile.Instructions.Length > 8192 ||
            profile.Tools.IsDefault || profile.Skills is { Length: > 8 } ||
            profile.Channels is { } channels && (channels.IsDefault || channels.Length > 8 ||
                channels.Distinct(StringComparer.Ordinal).Count() != channels.Length || channels.Any(channel => !ValidChannel(channel))) ||
            profile.MaxOutputTokens is < AgentOutputTokenLimits.Minimum or > AgentOutputTokenLimits.Maximum ||
            profile.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.') ||
            profile.Tools.Any(tool => tool is null || !ValidBinding(tool)) ||
            profile.Tools.Select(tool => tool.BindingId).Distinct(StringComparer.Ordinal).Count() != profile.Tools.Length ||
            profile.Skills?.Any(skill => skill is null || skill != WorkflowSkill.Create(skill.Id, skill.Version, skill.Name, skill.Instructions)) == true)
            throw new ArgumentException("Enter a valid profile with bounded instructions, unique tools, up to eight skills, and up to eight valid coordination-channel IDs.");
        ValidateFolder(folder);
    }

    private static bool ValidChannel(string channel) => channel.Length is > 0 and <= 64 && channel[0] is >= 'a' and <= 'z' &&
        channel.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');

    private static bool ValidBinding(ToolBindingRequest binding) => !string.IsNullOrWhiteSpace(binding.BindingId) &&
        binding.BindingId.Length <= 256 && binding.ContractHash.Length == 64 && binding.ContractHash.All(Uri.IsHexDigit);

    private static void ValidateFolder(string folder)
    {
        if (folder.Length > 256 || folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 8 ||
            folder.Split('/').Any(part => part is "." or ".." || part.Length > 64 || part.Contains('\\')))
            throw new ArgumentException("Use a folder path with at most eight short segments.");
    }
}

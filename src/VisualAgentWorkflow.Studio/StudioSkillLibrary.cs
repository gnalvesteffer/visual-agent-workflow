using System.Text.Json;
using VisualAgentWorkflow.Core;

namespace VisualAgentWorkflow.Studio;

/// <summary>Local authoring catalog. Published workflows embed exact text revisions and do not depend on this store.</summary>
public sealed class StudioSkillLibrary : ISkillLibrary
{
    private readonly object gate = new();
    private readonly string path;
    private List<SkillLibraryItem> items;

    public StudioSkillLibrary(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "skills.json");
        items = File.Exists(path) ? JsonSerializer.Deserialize<List<SkillLibraryItem>>(File.ReadAllText(path)) ?? [] : [];
        if (items.Any(item => item.Skill != WorkflowSkill.Create(item.Skill.Id, item.Skill.Version,
            item.Skill.Name, item.Skill.Instructions)))
            throw new InvalidDataException("The skill library contains a modified revision.");
    }

    public IReadOnlyList<SkillLibraryItem> List()
    {
        lock (gate) return items.OrderBy(item => item.Folder, StringComparer.Ordinal)
            .ThenBy(item => item.Skill.Name, StringComparer.Ordinal).ThenBy(item => item.Skill.Version, StringComparer.Ordinal).ToArray();
    }

    public void Add(string id, string version, string name, string instructions, string folder)
    {
        Validate(id, version, name, instructions, folder);
        var skill = WorkflowSkill.Create(id, version, name, instructions);
        lock (gate)
        {
            if (items.Any(item => item.Skill.Id == id && item.Skill.Version == version))
                throw new ArgumentException("This skill version already exists. Use a new version for edited instructions.");
            Save([.. items, new(skill, folder)]);
        }
    }

    public bool Move(string id, string version, string folder)
    {
        ValidateFolder(folder);
        lock (gate)
        {
            var index = items.FindIndex(item => item.Skill.Id == id && item.Skill.Version == version);
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
            var updated = items.Where(item => item.Skill.Id != id || item.Skill.Version != version).ToList();
            if (updated.Count == items.Count) return false;
            Save(updated);
            return true;
        }
    }

    private void Save(List<SkillLibraryItem> updated)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, path, true);
        items = updated;
    }

    internal static void Validate(string id, string version, string name, string instructions, string folder)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.') ||
            string.IsNullOrWhiteSpace(version) || version.Length > 64 || version.Contains('@') ||
            string.IsNullOrWhiteSpace(name) || name.Length > 128 || string.IsNullOrWhiteSpace(instructions) || instructions.Length > 8192)
            throw new ArgumentException("Enter a safe skill ID, version, name, and instructions up to 8,192 characters.");
        ValidateFolder(folder);
    }

    private static void ValidateFolder(string folder)
    {
        if (folder.Length > 256 || folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 8 ||
            folder.Split('/').Any(part => part is "." or ".." || part.Length > 64 || part.Contains('\\')))
            throw new ArgumentException("Use a folder path with at most eight short segments.");
    }
}

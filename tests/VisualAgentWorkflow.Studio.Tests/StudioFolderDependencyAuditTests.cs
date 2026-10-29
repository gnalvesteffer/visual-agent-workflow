using System.Collections.Immutable;
using System.Text;
using VisualAgentWorkflow.Blazor;
using VisualAgentWorkflow.Core;
using VisualAgentWorkflow.Studio;
using Xunit;

namespace VisualAgentWorkflow.Studio.Tests;

public sealed class StudioFolderDependencyAuditTests
{
    [Fact]
    public void Assess_reports_missing_and_stale_embedded_skill_sources()
    {
        var skill = WorkflowSkill.Create("research", "1.0.0", "Research", "Use evidence.");
        var definition = WorkflowTemplates.Create("uppercase") with
        {
            Skills = ImmutableSortedDictionary<string, WorkflowSkill>.Empty.Add("research@1.0.0", skill),
            Nodes = WorkflowTemplates.Create("uppercase").Nodes.SetItem("step-1",
                new(NodeTypes.Agent, "1.0.0", Agent: new AgentConfiguration("provider", null, "Research.", [])
                {
                    Skills = [new SkillReference(skill.Id, skill.Version, skill.ContentHash)]
                }))
        };
        var path = StudioProjectSourceArchive.SkillAssetPath(skill.Id, skill.Version, new string('a', 64));
        var source = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeSkillAsset(
            new SkillLibraryItem(skill, "skills")));
        var manifest = new StudioProjectManifest("1.2", "project", "Project", [
            new("skill", skill.Id, skill.Version, path, StudioProjectSourceArchive.ComputeContentHash(source))
        ]);

        Assert.Empty(StudioFolderDependencyAudit.Assess(definition, manifest, new Dictionary<string, byte[]>
        {
            [path] = source
        }));

        var stale = WorkflowSkill.Create(skill.Id, skill.Version, skill.Name, "Use newer evidence.");
        var staleSource = Encoding.UTF8.GetBytes(StudioProjectSourceArchive.SerializeSkillAsset(
            new SkillLibraryItem(stale, "skills")));
        var staleIssues = StudioFolderDependencyAudit.Assess(definition, manifest, new Dictionary<string, byte[]>
        {
            [path] = staleSource
        });
        Assert.Contains(staleIssues, issue => issue.Kind == StudioFolderDependencyIssueKind.Stale && issue.AssetKind == "skill");

        var missingIssues = StudioFolderDependencyAudit.Assess(definition, manifest, new Dictionary<string, byte[]>());
        Assert.Contains(missingIssues, issue => issue.Kind == StudioFolderDependencyIssueKind.Missing && issue.AssetKind == "skill");
    }
}

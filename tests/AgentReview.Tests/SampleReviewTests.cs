using System.Text.Json;
using AgentReview.Agents;
using AgentReview.Agents.Diff;
using Xunit;

namespace AgentReview.Tests;

/// <summary>
/// Pins the committed harness artifacts without any network: the sample diffs must
/// stay parseable, and the committed sample reviews must stay schema-valid Finding
/// arrays. The live half of the harness is the Orchestrator's --harness mode.
/// </summary>
public class SampleReviewTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static string SamplesDir
    {
        get
        {
            // Walk up from the test assembly to the repo root (marked by the slnx).
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "AgentReview.slnx")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            return Path.Combine(dir, "src", "AgentReview.Orchestrator", "samples");
        }
    }

    [Fact]
    public void CommittedSampleDiffs_Parse()
    {
        var diffs = Directory.GetFiles(SamplesDir, "*.diff");

        Assert.True(diffs.Length >= 2, "expected at least two committed sample diffs");
        foreach (var path in diffs)
        {
            var files = UnifiedDiffParser.Parse(File.ReadAllText(path));
            Assert.True(files.Count >= 1, $"{Path.GetFileName(path)} did not parse as a unified diff");
        }
    }

    [Fact]
    public void CommittedReviews_DeserializeAsSchemaValidFindings()
    {
        var reviewsDir = Path.Combine(SamplesDir, "reviews");
        if (!Directory.Exists(reviewsDir))
        {
            Assert.Skip("samples/reviews does not exist yet; run the Orchestrator with --harness to generate it.");
        }

        var reviews = Directory.GetFiles(reviewsDir, "*.review.json");
        Assert.True(reviews.Length >= 1, "reviews directory exists but holds no review files");

        foreach (var path in reviews)
        {
            var findings = JsonSerializer.Deserialize<List<Finding>>(File.ReadAllText(path), WebOptions);

            Assert.NotNull(findings);
            foreach (var f in findings)
            {
                Assert.False(string.IsNullOrWhiteSpace(f.Issue), $"empty issue in {Path.GetFileName(path)}");
                Assert.False(string.IsNullOrWhiteSpace(f.File), $"empty file in {Path.GetFileName(path)}");
                Assert.False(string.IsNullOrWhiteSpace(f.Source), $"empty source in {Path.GetFileName(path)}");
                Assert.True(f.Line >= 1, $"line {f.Line} out of range in {Path.GetFileName(path)}");
                Assert.True(Enum.IsDefined(f.Severity), $"undefined severity in {Path.GetFileName(path)}");
            }
        }
    }
}

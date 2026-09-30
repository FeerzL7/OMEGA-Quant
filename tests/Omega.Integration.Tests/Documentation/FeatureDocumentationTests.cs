using Omega.Features;
using Omega.Integration.Tests.Architecture;

namespace Omega.Integration.Tests.Documentation;

/// <summary>docs/FEATURES.md must describe every feature of the active set with its exact lookback.</summary>
public class FeatureDocumentationTests
{
    private static readonly string Catalog = File.ReadAllText(
        Path.Combine(SolutionProjects.Load().RootDirectory, "docs", "FEATURES.md"));

    [Fact]
    public void Every_feature_has_a_documented_section_with_its_lookback()
    {
        var missing = new List<string>();

        foreach (var definition in FeatureSets.V1().Definitions)
        {
            var heading = Catalog.Split('\n').FirstOrDefault(line =>
                line.StartsWith("### ", StringComparison.Ordinal) && line.Contains($"`{definition.Name}`", StringComparison.Ordinal));

            if (heading is null)
            {
                missing.Add($"{definition.Name}: no section");
                continue;
            }

            var section = Catalog[Catalog.IndexOf(heading, StringComparison.Ordinal)..];
            var next = section.IndexOf("\n### ", 1, StringComparison.Ordinal);
            section = next < 0 ? section : section[..next];

            var lookback = section.Split('\n').FirstOrDefault(line => line.Contains("**Lookback:**", StringComparison.Ordinal)) ?? string.Empty;
            var expected = $"{definition.Lookback} vela";

            // Sections that group several features list each one ("`name`: N velas"); single ones state "N velas".
            var documented = lookback.Contains($"`{definition.Name}`: {expected}", StringComparison.Ordinal)
                || (!lookback.Contains('`', StringComparison.Ordinal) && lookback.Contains(expected, StringComparison.Ordinal))
                || (lookback.Contains($"= {expected}", StringComparison.Ordinal) && !heading.Contains(',', StringComparison.Ordinal));

            if (!documented)
            {
                missing.Add($"{definition.Name}: lookback {definition.Lookback} not documented");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Active_feature_set_version_is_documented()
    {
        Assert.Contains($"`{FeatureSets.V1Version}`", Catalog, StringComparison.Ordinal);
    }
}

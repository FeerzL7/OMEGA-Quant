namespace Omega.Integration.Tests.Architecture;

/// <summary>
/// Enforces the dependency rules documented in docs/ARCHITECTURE.md.
/// A failing test here means a project reference broke the architecture.
/// </summary>
public class DependencyRulesTests
{
    private const string Core = "Omega.Core";
    private const string Ui = "Omega.UI";
    private const string Application = "Omega.Application";
    private const string Infrastructure = "Omega.Infrastructure";

    private static readonly string[] Hosts = ["Omega.Api", "Omega.UI", "Omega.Worker"];

    private static readonly string[] DomainModules =
    [
        "Omega.MarketData", "Omega.Features", "Omega.Strategy",
        "Omega.Risk", "Omega.Execution", "Omega.Backtesting",
    ];

    private static readonly string[] ForbiddenForUi =
    [
        "Omega.MarketData", "Omega.Features", "Omega.Strategy", "Omega.Risk",
        "Omega.Execution", "Omega.Backtesting", "Omega.Application", "Omega.Infrastructure", "Omega.Worker",
    ];

    // Database drivers and ORMs: only Omega.Infrastructure may use them.
    private static readonly string[] DatabasePackageFragments = ["Npgsql", "EntityFrameworkCore", "Dapper"];

    // Package name fragments that would give the UI direct access to the exchange,
    // the database or ML runtimes.
    private static readonly string[] ForbiddenUiPackageFragments =
    [
        "Binance", "Npgsql", "EntityFrameworkCore", "Microsoft.ML", "OnnxRuntime",
    ];

    // Canonical pipeline order (CLAUDE.md §3). A stage may depend on earlier
    // stages, never on later ones: market data cannot know about strategy,
    // and nothing upstream of Risk can reach Execution.
    private static readonly string[] PipelineOrder =
    [
        "Omega.MarketData", "Omega.Features", "Omega.Strategy", "Omega.Risk", "Omega.Execution",
    ];

    private readonly SolutionProjects _solution = SolutionProjects.Load();

    [Fact]
    public void Core_references_no_other_project()
    {
        Assert.Empty(_solution[Core].ProjectReferences);
    }

    [Fact]
    public void Core_references_no_package()
    {
        Assert.Empty(_solution[Core].PackageReferences);
    }

    [Fact]
    public void UI_does_not_reference_trading_infrastructure_or_worker_projects()
    {
        var violations = _solution[Ui].ProjectReferences.Intersect(ForbiddenForUi).ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void UI_does_not_reference_exchange_database_or_ml_packages()
    {
        var violations = _solution[Ui].PackageReferences
            .Where(package => ForbiddenUiPackageFragments.Any(fragment =>
                package.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_modules_do_not_reference_hosts_or_infrastructure()
    {
        var forbidden = Hosts.Append("Omega.Infrastructure").ToArray();

        var violations = DomainModules
            .SelectMany(module => _solution[module].ProjectReferences
                .Where(reference => forbidden.Contains(reference))
                .Select(reference => $"{module} -> {reference}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Application_does_not_reference_infrastructure_or_hosts()
    {
        var forbidden = Hosts.Append(Infrastructure).ToArray();

        var violations = _solution[Application].ProjectReferences.Intersect(forbidden).ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Infrastructure_does_not_reference_application_or_hosts()
    {
        var forbidden = Hosts.Append(Application).ToArray();

        var violations = _solution[Infrastructure].ProjectReferences.Intersect(forbidden).ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Only_infrastructure_references_database_packages()
    {
        var violations = _solution.Projects.Values
            .Where(project => !project.IsTestProject && project.Name != Infrastructure)
            .SelectMany(project => project.PackageReferences
                .Where(package => DatabasePackageFragments.Any(fragment =>
                    package.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                .Select(package => $"{project.Name} -> {package}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Pipeline_stages_do_not_reference_later_stages()
    {
        var violations = PipelineOrder
            .SelectMany((stage, index) => _solution[stage].ProjectReferences
                .Where(reference => Array.IndexOf(PipelineOrder, reference) > index)
                .Select(reference => $"{stage} -> {reference}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void No_project_references_a_host()
    {
        // Hosts are composition roots and entry points; nothing may depend on them,
        // except test projects that test that specific host.
        var violations = _solution.Projects.Values
            .SelectMany(project => project.ProjectReferences
                .Where(reference => Hosts.Contains(reference))
                .Where(reference => project.Name != $"{reference}.Tests")
                .Select(reference => $"{project.Name} -> {reference}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Production_projects_do_not_reference_test_projects()
    {
        var violations = _solution.Projects.Values
            .Where(project => !project.IsTestProject)
            .SelectMany(project => project.ProjectReferences
                .Where(reference => reference.EndsWith(".Tests", StringComparison.Ordinal))
                .Select(reference => $"{project.Name} -> {reference}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Project_reference_graph_has_no_cycles()
    {
        var cycle = FindCycle(_solution.Projects);

        Assert.True(cycle is null, $"Circular dependency: {string.Join(" -> ", cycle ?? [])}");
    }

    [Fact]
    public void Every_referenced_project_exists_in_the_repository()
    {
        var missing = _solution.Projects.Values
            .SelectMany(project => project.ProjectReferences
                .Where(reference => !_solution.Projects.ContainsKey(reference))
                .Select(reference => $"{project.Name} -> {reference}"))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_project_is_part_of_the_solution_file()
    {
        var solutionText = File.ReadAllText(Path.Combine(_solution.RootDirectory, "OMEGA.sln"));

        var missing = _solution.Projects.Keys
            .Where(name => !solutionText.Contains($"\"{name}\"", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(missing);
    }

    private static List<string>? FindCycle(IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);

        List<string>? Visit(string name)
        {
            if (onPath.Contains(name))
            {
                return [.. path.SkipWhile(node => node != name), name];
            }

            if (!visited.Add(name) || !projects.TryGetValue(name, out var project))
            {
                return null;
            }

            path.Add(name);
            onPath.Add(name);

            foreach (var reference in project.ProjectReferences)
            {
                if (Visit(reference) is { } cycle)
                {
                    return cycle;
                }
            }

            path.RemoveAt(path.Count - 1);
            onPath.Remove(name);
            return null;
        }

        return projects.Keys.Select(Visit).FirstOrDefault(cycle => cycle is not null);
    }
}

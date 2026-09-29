using System.Xml.Linq;

namespace Omega.Integration.Tests.Architecture;

/// <summary>
/// Reads the solution's .csproj files from disk and exposes their project and
/// package references. Reading the files (instead of loading assemblies) lets the
/// architecture rules cover every project without referencing any of them.
/// </summary>
internal sealed class SolutionProjects
{
    private const string SolutionFileName = "OMEGA.sln";

    private SolutionProjects(string rootDirectory, IReadOnlyDictionary<string, ProjectInfo> projects)
    {
        RootDirectory = rootDirectory;
        Projects = projects;
    }

    public string RootDirectory { get; }

    public IReadOnlyDictionary<string, ProjectInfo> Projects { get; }

    public static SolutionProjects Load()
    {
        var root = LocateRepositoryRoot();

        var projects = new[] { "src", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path))
            .Select(ProjectInfo.Parse)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);

        return new SolutionProjects(root, projects);
    }

    public ProjectInfo this[string name] => Projects.TryGetValue(name, out var project)
        ? project
        : throw new InvalidOperationException($"Project '{name}' was not found under src/ or tests/.");

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");

    private static string LocateRepositoryRoot()
    {
        var starts = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

        foreach (var start in starts)
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException($"Could not locate {SolutionFileName} above the test output or working directory.");
    }
}

internal sealed record ProjectInfo(
    string Name,
    string Path,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences)
{
    public bool IsTestProject => Name.EndsWith(".Tests", StringComparison.Ordinal);

    public static ProjectInfo Parse(string path)
    {
        var document = XDocument.Load(path);

        var projectReferences = document.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(include => System.IO.Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToList();

        var packageReferences = document.Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .ToList();

        return new ProjectInfo(
            System.IO.Path.GetFileNameWithoutExtension(path),
            path,
            projectReferences,
            packageReferences);
    }
}

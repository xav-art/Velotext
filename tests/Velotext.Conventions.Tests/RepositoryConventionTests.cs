// Velotext: enforces repository rules that the compiler cannot: file descriptions and no em dash anywhere.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.RegularExpressions;

namespace Velotext.Conventions.Tests;

/// <summary>
/// Repository conventions. Every source file starts with a one-line description, every test class has a
/// summary, and no file contains the em dash character (project writing rule).
/// </summary>
public sealed partial class RepositoryConventionTests
{
    private static readonly string[] s_textExtensions = [".cs", ".xaml", ".csproj", ".props", ".targets", ".json", ".md", ".txt", ".yml", ".yaml", ".ps1", ".config", ".editorconfig", ".gitignore", ".gitattributes", ".manifest", ".slnx"];
    private static readonly string[] s_skippedFolders = ["bin", "obj", ".git", ".dotnet", ".vs", "TestResults", "artifacts", "docs", ".claude", ".sf", "node_modules"];

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Velotext.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private static IEnumerable<string> Files(params string[] extensions) =>
        Directory.EnumerateFiles(RepositoryRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(RepositoryRoot, path).Split(Path.DirectorySeparatorChar).Any(part => s_skippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || extensions.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void No_file_contains_an_em_dash()
    {
        var emDash = (char)0x2014;
        var offenders = Files(s_textExtensions)
            .Where(path => File.ReadAllText(path).Contains(emDash, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_csharp_file_starts_with_a_description_and_the_licence_line()
    {
        var offenders = Files(".cs")
            .Where(path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var lines = File.ReadLines(path).Take(2).ToList();
                return lines.Count < 2
                    || !HeaderPattern().IsMatch(lines[0])
                    || lines[1] != "// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.";
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_xaml_and_project_file_starts_with_a_description_comment()
    {
        var offenders = Files(".xaml", ".csproj", ".props")
            .Where(path =>
            {
                var first = File.ReadLines(path).FirstOrDefault(line => !line.StartsWith("<?xml", StringComparison.Ordinal)) ?? string.Empty;
                return !first.StartsWith("<!-- Velotext: ", StringComparison.Ordinal) || first.Length < 30;
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Scripts_and_workflows_start_with_a_description()
    {
        var offenders = Files(".ps1", ".yml", ".yaml")
            .Where(path => !(File.ReadLines(path).FirstOrDefault() ?? string.Empty).StartsWith("# Velotext: ", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_test_class_has_a_summary()
    {
        var offenders = Files(".cs")
            .Where(path => Path.GetRelativePath(RepositoryRoot, path).StartsWith("tests", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("[Fact", StringComparison.Ordinal) || File.ReadAllText(path).Contains("[Theory", StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                var classIndex = TestClassPattern().Match(text);
                return !classIndex.Success || !text[..classIndex.Index].TrimEnd().EndsWith("</summary>", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"^// Velotext: \S.{10,}$")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"^public sealed (partial )?class \w+", RegexOptions.Multiline)]
    private static partial Regex TestClassPattern();

    [Fact]
    public void Third_party_notices_name_every_package_that_ships_with_the_app()
    {
        // The app's lock file lists every package in the installer; each one needs its license notice.
        var notices = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Velotext.App", "THIRD-PARTY-NOTICES.txt"));
        using var lockFile = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Velotext.App", "packages.lock.json")));
        var packages = lockFile.RootElement.GetProperty("dependencies").EnumerateObject()
            .SelectMany(framework => framework.Value.EnumerateObject())
            .Where(package => package.Value.GetProperty("type").GetString() != "Project")
            .Select(package => package.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(packages);
        Assert.DoesNotContain(packages, package => !notices.Contains(package, StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "LICENSE")));
    }
}

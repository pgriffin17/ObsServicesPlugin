using NUnit.Framework;
using ObsServices.Core.Agent;
using ObsServices.Core.Update;

namespace ObsServices.Core.Tests;

/// <summary>
/// The in-app updater against a local git remote: clone, check, sync in both modes, and a real
/// <c>dotnet build</c> of a stand-in project with a <c>StageForNina</c> target. Skips when git is missing.
/// </summary>
public class UpdateTests {
    private string root = string.Empty;
    private string origin = string.Empty;
    private string author = string.Empty;

    [SetUp]
    public async Task MakeRemote() {
        root = Path.Combine(Path.GetTempPath(), "obsservices-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        origin = Path.Combine(root, "origin.git");
        author = Path.Combine(root, "author");

        var probe = await TryGit(root, "--version");

        if (probe == null) {
            Assert.Ignore("git is not installed");
        }

        await Git(root, "init", "--quiet", "--bare", "--initial-branch=main", origin);
        await Git(root, "clone", "--quiet", origin, author);
        await Git(author, "checkout", "--quiet", "-b", "dev");
        WriteProject(author, "1");
        await Commit(author, "first");
        await Git(author, "push", "--quiet", "-u", "origin", "dev");
    }

    [TearDown]
    public void RemoveAll() {
        if (Directory.Exists(root)) {
            try {
                Directory.Delete(root, recursive: true);
            } catch (IOException) {
            }
        }
    }

    private static async Task<string?> TryGit(string cwd, params string[] args) {
        try {
            var r = await CommandRunner.RunAsync("git", args, cwd);
            return r.Ok ? r.Text : null;
        } catch (CommandException) {
            return null;
        }
    }

    private static Task<CommandResult> Git(string cwd, params string[] args) =>
        CommandRunner.RequireAsync("git", new[] { "-c", "user.name=test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args), cwd);

    private static async Task<string> Commit(string cwd, string message) {
        await Git(cwd, "add", "-A");
        await Git(cwd, "commit", "--quiet", "-m", message);
        return (await Git(cwd, "rev-parse", "HEAD")).Text;
    }

    /// <summary>A stand-in for the plugin project: a tiny library whose StageForNina target behaves like the real one.</summary>
    private static void WriteProject(string checkout, string marker) {
        var dir = Path.Combine(checkout, "src", "ObsServicesPlugin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ObsServicesPlugin.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
                <AssemblyName>StandIn</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <PluginPackageFile Include="$(TargetPath)" Destination="$(TargetFileName)" />
                <PluginPackageFile Include="$(MSBuildProjectDirectory)/marker.txt" Destination="marker.txt" />
              </ItemGroup>
              <Target Name="StageForNina" DependsOnTargets="Build">
                <MakeDir Directories="$(NinaStageDirectory)" />
                <Copy SourceFiles="@(PluginPackageFile)" DestinationFiles="@(PluginPackageFile->'$(NinaStageDirectory)/%(Destination)')" />
                <WriteLinesToFile File="$(NinaStageDirectory)/.obsservices-files" Lines="@(PluginPackageFile->'%(Destination)');.obsservices-files" Overwrite="true" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), "public static class Marker { public const string Value = \"" + marker + "\"; }\n");
        File.WriteAllText(Path.Combine(dir, "marker.txt"), marker);
        File.WriteAllText(Path.Combine(checkout, ".gitignore"), "bin/\nobj/\n");
    }

    private UpdateOptions Options(string source, bool managed, string? branch = null) => new() {
        RepositoryUrl = origin,
        Branch = branch ?? "dev",
        SourceDirectory = source,
        Managed = managed,
        StageDirectory = Path.Combine(root, "stage"),
    };

    [Test]
    public async Task ManagedCloneFollowsTheBranchAndStagesABuild() {
        var log = new List<string>();
        var updater = new PluginUpdater(Options(Path.Combine(root, "managed"), managed: true));
        updater.Log += log.Add;

        var check = await updater.CheckAsync();
        Assert.That(check.UpToDate, Is.True, "a fresh clone is at the branch tip");
        Assert.That(check.Remote.Branch, Is.EqualTo("dev"));
        Assert.That(check.Remote.Subject, Is.EqualTo("first"));
        Assert.That(log.Any(l => l.StartsWith("cloning")), Is.True);

        // the branch moves on
        WriteProject(author, "2");
        var second = await Commit(author, "second");
        await Git(author, "push", "--quiet");

        check = await updater.CheckAsync();
        Assert.That(check.Behind, Is.EqualTo(1));
        Assert.That(check.Remote.Sha, Is.EqualTo(second));
        Assert.That(updater.HasStagedPackage(), Is.False);

        var staged = await updater.UpdateAsync();
        Assert.That(staged.Commit.Sha, Is.EqualTo(second));
        Assert.That(staged.Files, Is.EquivalentTo(new[] { "StandIn.dll", "marker.txt", ".obsservices-files" }));
        Assert.That(File.ReadAllText(Path.Combine(root, "stage", "marker.txt")), Is.EqualTo("2"));
        Assert.That(updater.HasStagedPackage(), Is.True);
        Assert.That(log.Any(l => l.Contains("StandIn ->") || l.Contains("Staged") || l.Contains("staged")), Is.True, string.Join("\n", log));

        // a second update over the stage replaces it rather than piling up
        WriteProject(author, "3");
        await Commit(author, "third");
        await Git(author, "push", "--quiet");
        staged = await updater.UpdateAsync();
        Assert.That(staged.Commit.Subject, Is.EqualTo("third"));
        Assert.That(File.ReadAllText(Path.Combine(root, "stage", "marker.txt")), Is.EqualTo("3"));

        // the managed clone is reset even when something scribbled in it
        File.WriteAllText(Path.Combine(root, "managed", "src", "ObsServicesPlugin", "marker.txt"), "scribble");
        var synced = await new GitRepository(Path.Combine(root, "managed")).SyncAsync("dev", managed: true);
        Assert.That(synced.Subject, Is.EqualTo("third"));
        Assert.That(File.ReadAllText(Path.Combine(root, "managed", "src", "ObsServicesPlugin", "marker.txt")), Is.EqualTo("3"));
    }

    [Test]
    public async Task AUsersCheckoutIsOnlyFastForwarded() {
        var mine = Path.Combine(root, "mine");
        await Git(root, "clone", "--quiet", "--branch", "dev", origin, mine);
        var repo = new GitRepository(mine);

        // dirty: refused, untouched
        File.AppendAllText(Path.Combine(mine, "src", "ObsServicesPlugin", "marker.txt"), "+local");
        var dirty = Assert.ThrowsAsync<CommandException>(() => repo.SyncAsync("dev", managed: false));
        Assert.That(dirty!.Message, Does.Contain("uncommitted"));
        Assert.That(File.ReadAllText(Path.Combine(mine, "src", "ObsServicesPlugin", "marker.txt")), Is.EqualTo("1+local"));
        await Git(mine, "checkout", "--quiet", "--", ".");

        // clean and behind: fast-forwarded
        WriteProject(author, "2");
        await Commit(author, "second");
        await Git(author, "push", "--quiet");
        var synced = await repo.SyncAsync("dev", managed: false);
        Assert.That(synced.Subject, Is.EqualTo("second"));

        // diverged: refused, the local commit survives
        WriteProject(mine, "mine");
        var local = await Commit(mine, "my own work");
        WriteProject(author, "3");
        await Commit(author, "third");
        await Git(author, "push", "--quiet");
        var diverged = Assert.ThrowsAsync<CommandException>(() => repo.SyncAsync("dev", managed: false));
        Assert.That(diverged!.Message, Does.Contain("diverged"));
        Assert.That((await repo.HeadAsync()).Sha, Is.EqualTo(local));

        // another branch is checked out when asked for, tracking origin
        await Git(author, "checkout", "--quiet", "-b", "nina-3.2");
        await Git(author, "push", "--quiet", "-u", "origin", "nina-3.2");
        await Git(mine, "reset", "--quiet", "--hard", "origin/dev");
        var other = await repo.SyncAsync("nina-3.2", managed: false);
        Assert.That(other.Branch, Is.EqualTo("nina-3.2"));
        Assert.That(await repo.CurrentBranchAsync(), Is.EqualTo("nina-3.2"));

        // and a folder that is not a checkout is refused rather than cloned over
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "x"), "x");
        Assert.ThrowsAsync<CommandException>(() => new PluginUpdater(Options(plain, managed: false)).CheckAsync());
        Assert.ThrowsAsync<CommandException>(() => new PluginUpdater(Options(plain, managed: true)).CheckAsync());
    }

    [Test]
    public void StaleFilesAreTheAssembliesTheManifestDoesNotName() {
        var dir = Path.Combine(root, "plugin");
        Directory.CreateDirectory(dir);
        Assert.That(StaleFiles.Find(dir), Is.Empty, "no manifest, nothing is judged");

        File.WriteAllLines(Path.Combine(dir, StaleFiles.ManifestName), new[] { "ObsServicesPlugin.dll", "ObsServices.Core.dll", StaleFiles.ManifestName });
        foreach (var f in new[] { "ObsServicesPlugin.dll", "ObsServices.Core.dll", "OldHelper.dll", "OldHelper.pdb", "notes.txt" }) {
            File.WriteAllText(Path.Combine(dir, f), f);
        }

        Assert.That(StaleFiles.Find(dir).Select(Path.GetFileName), Is.EqualTo(new[] { "OldHelper.dll", "OldHelper.pdb" }));

        // the stage is cleared only when the manifest proves it is ours
        var stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "somebody-elses.dll"), "x");
        var updater = new PluginUpdater(Options(Path.Combine(root, "m"), managed: true));
        Assert.Throws<CommandException>(updater.ClearStage);
        File.WriteAllLines(Path.Combine(stage, StaleFiles.ManifestName), new[] { "somebody-elses.dll", StaleFiles.ManifestName });
        updater.ClearStage();
        Assert.That(Directory.EnumerateFileSystemEntries(stage), Is.Empty);
    }

    [Test]
    public void BuildLineAndInstalledBuild() {
        var updater = new PluginUpdater(Options(Path.Combine(root, "m"), managed: true));
        var args = updater.BuildArguments("p.csproj");
        Assert.That(args, Does.Contain("-t:StageForNina"));
        Assert.That(args, Does.Contain($"-p:NinaStageDirectory={Path.Combine(root, "stage")}"));
        Assert.That(args.Skip(2).Take(2), Is.EqualTo(new[] { "-c", "Release" }));

        // the core is not stamped (only the plugin project imports the target), so the build is unknown but readable
        var build = InstalledBuild.Of(typeof(SiteAgent).Assembly);
        Assert.That(build.Known, Is.False);
        Assert.That(build.ToString(), Does.Contain("unknown"));

        var known = new InstalledBuild("1.0.0.0", "0102f69abcdef0123456789abcdef0123456789a", "dev", DateTimeOffset.UnixEpoch, false);
        Assert.That(known.Short, Is.EqualTo("0102f69"));
        Assert.That(known.Is(new Commit("0102f69abcdef0123456789abcdef0123456789a", "dev", DateTimeOffset.UnixEpoch, "x")), Is.True);
        Assert.That((known with { Dirty = true }).Is(new Commit("0102f69abcdef0123456789abcdef0123456789a", "dev", DateTimeOffset.UnixEpoch, "x")), Is.False);
    }
}

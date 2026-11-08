using System.Diagnostics;
using System.Text;

namespace ObsServices.Core.Update;

/// <summary>What a tool printed and how it ended.</summary>
public sealed record CommandResult(int ExitCode, IReadOnlyList<string> Output, IReadOnlyList<string> Errors) {
    public bool Ok => ExitCode == 0;

    /// <summary>Standard output as one trimmed string, for single-line answers such as a commit id.</summary>
    public string Text => string.Join("\n", Output).Trim();

    /// <summary>The last few lines of both streams, for an error message.</summary>
    public string Tail(int lines = 6) {
        var all = Errors.Concat(Output).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return string.Join("\n", all.Skip(Math.Max(0, all.Count - lines)));
    }
}

/// <summary>A tool that could not be started or failed.</summary>
public sealed class CommandException : Exception {
    public CommandException(string message, CommandResult? result = null) : base(message) {
        Result = result;
    }

    public CommandResult? Result { get; }
}

/// <summary>Runs git and dotnet without a shell, streaming their output to a log and killing them on cancel.</summary>
public static class CommandRunner {
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, string? workingDirectory = null, Action<string>? log = null, TimeSpan? timeout = null, CancellationToken ct = default) {
        var info = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in arguments) {
            info.ArgumentList.Add(a);
        }

        // never prompt: a missing credential must fail, not hang N.I.N.A.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var output = new List<string>();
        var errors = new List<string>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => {
            if (e.Data != null) {
                lock (output) output.Add(e.Data);
                log?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data != null) {
                lock (errors) errors.Add(e.Data);
                log?.Invoke(e.Data);
            }
        };
        process.Exited += (_, _) => done.TrySetResult(true);

        try {
            if (!process.Start()) {
                throw new CommandException($"{executable} did not start");
            }
        } catch (CommandException) {
            throw;
        } catch (Exception e) {
            throw new CommandException($"cannot run {executable}: {e.Message}", null);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (timeout != null) {
            limit.CancelAfter(timeout.Value);
        }

        try {
            await process.WaitForExitAsync(limit.Token);
        } catch (OperationCanceledException) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception) {
            }

            if (ct.IsCancellationRequested) {
                throw;
            }

            throw new CommandException($"{executable} {string.Join(' ', arguments)} took longer than {timeout}");
        }

        // WaitForExitAsync returns at exit; the stream readers may still be draining
        await Task.WhenAny(done.Task, Task.Delay(2000, CancellationToken.None));
        process.WaitForExit();

        lock (output) lock (errors) {
            return new CommandResult(process.ExitCode, output.ToList(), errors.ToList());
        }
    }

    /// <summary>Runs and throws with the tool's last lines when it fails.</summary>
    public static async Task<CommandResult> RequireAsync(string executable, IEnumerable<string> arguments, string? workingDirectory = null, Action<string>? log = null, TimeSpan? timeout = null, CancellationToken ct = default) {
        var args = arguments.ToList();
        var result = await RunAsync(executable, args, workingDirectory, log, timeout, ct);

        if (!result.Ok) {
            throw new CommandException($"{executable} {string.Join(' ', args)} failed ({result.ExitCode}): {result.Tail()}", result);
        }

        return result;
    }
}

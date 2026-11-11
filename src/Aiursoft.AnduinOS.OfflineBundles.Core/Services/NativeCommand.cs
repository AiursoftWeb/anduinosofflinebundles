using System.Diagnostics;
using System.Text;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed record CommandResult(int ExitCode, string Output, string Error);

/// <summary>Runs bounded native tools without a shell or changes to the host package database.</summary>
public static class NativeCommand
{
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        string? directory = null, IDictionary<string, string?>? environment = null,
        Stream? output = null, CancellationToken cancellationToken = default, bool requireSuccess = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.Environment["LC_ALL"] = "C";
        process.StartInfo.Environment["LANG"] = "C";
        if (environment != null)
        {
            foreach (var item in environment)
            {
                process.StartInfo.Environment[item.Key] = item.Value;
            }
        }
        process.Start();
        var stdout = CancelOnFailureAsync(output == null
            ? ReadBoundedAsync(process.StandardOutput, timeout.Token)
            : CopyOutputAsync(process.StandardOutput.BaseStream, output, timeout.Token), timeout);
        var stderr = CancelOnFailureAsync(ReadBoundedAsync(process.StandardError, timeout.Token), timeout);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var result = new CommandResult(process.ExitCode, await stdout, await stderr);
        if (requireSuccess && result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} failed ({result.ExitCode}): {result.Error}\n{result.Output}");
        }
        return result;
    }

    private static async Task<string> CancelOnFailureAsync(Task<string> task, CancellationTokenSource source)
    {
        try
        {
            return await task;
        }
        catch
        {
            // Stop the process even if it fills stdout while the reader has already failed.
            await source.CancelAsync();
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        const int maximum = 16 * 1024 * 1024;
        var buffer = new char[8192];
        var result = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + read > maximum)
            {
                throw new InvalidDataException("Native command output exceeded the safety limit.");
            }
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    private static async Task<string> CopyOutputAsync(Stream source, Stream destination, CancellationToken token)
    {
        await source.CopyToAsync(destination, token);
        return string.Empty;
    }
}

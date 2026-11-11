using System.CommandLine;
using Aiursoft.CommandFramework.Framework;

namespace Aiursoft.AnduinOS.OfflineBundles.Handlers;

public abstract class BundleCommandHandler : CommandHandlerBuilder
{
    protected static readonly Option<string> FileOption = new("--file") { Description = "The atgz file.", Required = true };
    protected static readonly Option<string> KeyringOption = new("--keyring") { Description = "External trusted OpenPGP public keyring.", Required = true };
    protected static readonly Option<bool> JsonOption = new("--json") { Description = "Write a JSON result to stdout; diagnostics go to stderr." };
    protected abstract IEnumerable<Option> Options { get; }
    protected abstract Task<int> ExecuteAsync(ParseResult result, CancellationToken token);

    public override Command BuildAsCommand()
    {
        var command = base.BuildAsCommand();
        foreach (var option in Options)
        {
            command.Options.Add(option);
        }
        command.SetAction(async (result, token) =>
        {
            try
            {
                return await ExecuteAsync(result, token);
            }
            catch (OperationCanceledException)
            {
                await Console.Error.WriteLineAsync("Operation cancelled; no system installation was performed.");
                return 130;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or
                                             System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException or
                                             System.ComponentModel.Win32Exception)
            {
                await Console.Error.WriteLineAsync(error.Message);
                return 1;
            }
        });
        return command;
    }
}

namespace Obkhodiki.Core.Engine;

public interface IEngineRunner
{
    /// <summary>Starts winws with the given resolved arguments, stopping any instance this runner owns.</summary>
    Task StartAsync(IReadOnlyList<string> args, CancellationToken ct);

    Task StopAsync();
}

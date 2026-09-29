using CompatibilityLab.Store;

namespace CompatibilityLab.Engine;

public sealed class MatrixBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    public MatrixBackgroundService(IServiceProvider services) => _services = services;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<MatrixRunner>();
                runner.DrainAll(maxCells: 64);
            }
            catch (ProcessCrashSimulatedException)
            {
                // crash hook only used in tests; ignore in the hosted loop
            }
            catch
            {
                // never let the background loop die; retry next tick
            }
            await Task.Delay(200, stoppingToken);
        }
    }
}

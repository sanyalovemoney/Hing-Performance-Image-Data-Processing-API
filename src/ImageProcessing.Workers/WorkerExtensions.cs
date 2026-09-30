using Microsoft.Extensions.DependencyInjection;

namespace ImageProcessing.Workers;

public static class WorkerExtensions
{
    /// <summary>Registers all hosted workers. Core services (queue, store, storage) are wired by the host app.</summary>
    public static IServiceCollection AddJobWorkers(this IServiceCollection services)
    {
        services.AddHostedService<JobConsumerWorker>();
        services.AddHostedService<TempCleanupWorker>();
        return services;
    }
}

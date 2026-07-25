using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHook2StreamInfrastructure(
    builder.Configuration,
    builder.Environment,
    includeBilling: false);

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrapper");
var dbContext = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();

var migrations = dbContext.Database.GetMigrations();
if (migrations.Any())
{
    logger.LogInformation("Applying PostgreSQL migrations.");
    await dbContext.Database.MigrateAsync();
}
else
{
    logger.LogWarning("No migrations are present yet; creating the initial development schema.");
    await dbContext.Database.EnsureCreatedAsync();
}

logger.LogInformation("Ensuring the object-storage bucket and configured policies.");
await scope.ServiceProvider.GetRequiredService<IObjectStorage>().EnsureBucketAsync(CancellationToken.None);
logger.LogInformation("Hook2Stream bootstrap completed.");

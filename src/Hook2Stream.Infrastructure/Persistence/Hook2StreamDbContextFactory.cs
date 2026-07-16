using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hook2Stream.Infrastructure.Persistence;

public sealed class Hook2StreamDbContextFactory : IDesignTimeDbContextFactory<Hook2StreamDbContext>
{
    public Hook2StreamDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("HOOK2STREAM_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=hook2stream;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new Hook2StreamDbContext(options);
    }
}

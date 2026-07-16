using Hook2Stream.Infrastructure;
using Hook2Stream.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHook2StreamInfrastructure(builder.Configuration);
builder.Services.AddHostedService<MediaJobWorker>();

var host = builder.Build();
host.Run();

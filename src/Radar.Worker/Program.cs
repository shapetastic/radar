using Radar.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRadarWorker(builder.Configuration);

var host = builder.Build();
host.Run();

using FluxVault.Service;
using FluxVault.Windows.Capture;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FluxVault Service");
builder.Services.AddSingleton(CapturePipelinePlanner.CreateDefault());
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

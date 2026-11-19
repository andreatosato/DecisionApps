var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Pong_Web>("pong")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.Build().Run();

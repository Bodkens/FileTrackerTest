using FileTracker;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<FileTrackerService>();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();



app.MapGet("/changes", async ([FromQuery]string folder, FileTrackerService tracker, CancellationToken cancellationToken) =>
{
    try
    {
        var (addedFiles, changedFiles, deletedFiles) = await tracker.UpdateMemory(folder, cancellationToken);
        return Results.Ok(new { addedFiles, changedFiles, deletedFiles, versions = tracker.GetFileVersionNumbers(folder) });
    }
    catch (DirectoryNotFoundException ex) { return Results.NotFound(ex.Message); }
});


app.Run();

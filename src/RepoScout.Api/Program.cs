using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddScoped<IRepoAnalyzer, MockRepoAnalyzer>();
builder.Services.AddGitHubClient();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();

public partial class Program;

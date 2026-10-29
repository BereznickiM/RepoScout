using RepoScout.Api.Agent;
using RepoScout.Api.Ai;
using RepoScout.Api.Analysis;
using RepoScout.Api.GitHub;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddOptions<AnalysisOptions>()
    .BindConfiguration(AnalysisOptions.SectionName);
builder.Services.AddScoped<IRepoAnalyzer, RepoAgent>();
builder.Services.AddGitHubClient();
builder.Services.AddGeminiChatClient();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();

public partial class Program;

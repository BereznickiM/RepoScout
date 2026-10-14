namespace RepoScout.Api.GitHub;

public static class GitHubServiceCollectionExtensions
{
    public static IHttpClientBuilder AddGitHubClient(this IServiceCollection services)
    {
        services.AddTransient<IGitHubClient>(sp => sp.GetRequiredService<GitHubClient>());

        return services.AddHttpClient<GitHubClient>(c =>
        {
            c.BaseAddress = new Uri("https://api.github.com/");
            c.DefaultRequestHeaders.UserAgent.ParseAdd("RepoScout");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        });
    }
}

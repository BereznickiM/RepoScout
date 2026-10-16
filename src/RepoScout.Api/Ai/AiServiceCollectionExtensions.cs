using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace RepoScout.Api.Ai;

public static class AiServiceCollectionExtensions
{
    public static IHttpClientBuilder AddGeminiChatClient(this IServiceCollection services)
    {
        services.AddOptions<AiModelOptions>()
            .BindConfiguration(AiModelOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddTransient<IChatClient>(sp => sp.GetRequiredService<GeminiChatClient>());

        return services.AddHttpClient<GeminiChatClient>((sp, c) =>
        {
            c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
            c.DefaultRequestHeaders.Add("Api-Revision", "2026-05-20");
            c.DefaultRequestHeaders.Add("x-goog-api-key", sp.GetRequiredService<IOptions<AiModelOptions>>().Value.ApiKey);
        });
    }
}

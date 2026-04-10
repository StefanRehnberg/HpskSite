using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using HpskSite.Services.AiChat;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Composers
{
    public class AiChatComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.Configure<AiChatOptions>(
                builder.Config.GetSection("AiChat"));

            builder.Services.AddSingleton<KnowledgeBaseService>();

            builder.Services.AddHttpClient("AiChat");
            builder.Services.AddScoped<AiChatService>();
        }
    }
}

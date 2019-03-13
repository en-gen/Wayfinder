using System;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Flow.Api.Infrastructure.Swagger
{
    public class ConfigureSwaggerOptions : IConfigureOptions<SwaggerGenOptions>
    {
        private IApiVersionDescriptionProvider Provider { get; }

        public ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider)
        {
            Provider = provider;
        }

        public void Configure(SwaggerGenOptions opts)
        {
            opts.DescribeAllEnumsAsStrings();

            foreach (var desc in Provider.ApiVersionDescriptions)
            {
                opts.SwaggerDoc(
                    desc.GroupName,
                    new Info
                    {
                        Title = $"Flow v{desc.ApiVersion}",
                        Version = desc.ApiVersion.ToString()
                    });
            }
        }
    }
}

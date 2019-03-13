using Flow.Api.Infrastructure.Swagger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Flow.Api
{
    public class Startup
    {
        private IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services
                .AddMvcCore();

            services
                .AddMvc()
                .SetCompatibilityVersion(CompatibilityVersion.Version_2_1);
            
            services
                .AddApiVersioning()
                .AddVersionedApiExplorer(options => options.GroupNameFormat = "'v'VVV")

                .AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>()
                .AddSwaggerGen();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, IApiVersionDescriptionProvider versions)
        {
            app
                .UseResponseCompression()
                .UseResponseCompression()
                .UseSwagger(opts => opts.RouteTemplate = "swagger/flow/{documentName}/swagger.json")
                .UseSwaggerUI(opts =>
                {
                    opts.RoutePrefix = "explorer/flow";
                    opts.DisplayRequestDuration();

                    foreach (var desc in versions.ApiVersionDescriptions)
                    {
                        opts.SwaggerEndpoint(
                            $"/swagger/flow/{desc.GroupName}/swagger.json",
                            $"Flow {desc.GroupName}");
                    }
                })
                .UseMvc();
        }
    }
}

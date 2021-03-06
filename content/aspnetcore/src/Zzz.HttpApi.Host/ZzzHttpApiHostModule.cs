using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Zzz.EntityFrameworkCore;
using Zzz.MultiTenancy;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic;
using Microsoft.OpenApi.Models;
using Volo.Abp;
using Volo.Abp.Account.Web;
using Volo.Abp.AspNetCore.Authentication.JwtBearer;
using Volo.Abp.AspNetCore.MultiTenancy;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.UI.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Basic.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Shared;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.Autofac;
using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Volo.Abp.Swashbuckle;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;
using Zzz.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Serilog;
using Zzz.Extensions;
using Hangfire;
using Hangfire.Redis;
using Zzz.Extensions.Filters;
using Volo.Abp.Auditing;
using Volo.Abp.BackgroundJobs;
using Swashbuckle.AspNetCore.SwaggerUI;
using Volo.Abp.Json;
using Volo.Abp.Settings;

namespace Zzz
{
    [DependsOn(
        typeof(ZzzHttpApiModule),
        typeof(AbpAutofacModule),
        typeof(AbpAspNetCoreMultiTenancyModule),
        typeof(ZzzApplicationModule),
        typeof(ZzzEntityFrameworkCoreDbMigrationsModule),
        typeof(AbpAspNetCoreMvcUiBasicThemeModule),
        typeof(AbpAspNetCoreAuthenticationJwtBearerModule),
        typeof(AbpAccountWebIdentityServerModule),
        typeof(AbpAspNetCoreSerilogModule),
        typeof(AbpSwashbuckleModule)
    )]
    public class ZzzHttpApiHostModule : AbpModule
    {
        private const string DefaultCorsPolicyName = "Default";

        public override void OnPreApplicationInitialization(ApplicationInitializationContext context)
        {
            var app = context.GetApplicationBuilder();
            app.ApplicationServices.GetService<ISettingDefinitionManager>().Get(LocalizationSettingNames.DefaultLanguage).DefaultValue = "zh-Hans";
            app.UseHangfireDashboard("/hangfire", new DashboardOptions()
            {
                Authorization = new[] { new CustomHangfireAuthorizeFilter() }
            });
            context.ServiceProvider.CreateRecurringJob();
        }

        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            var configuration = context.Services.GetConfiguration();
            var hostingEnvironment = context.Services.GetHostingEnvironment();
            ConfigureJson();
            ConfigureOptions(context);
            ConfigureBundles();
            ConfigureUrls(configuration);
            ConfigureConventionalControllers();
            ConfigureJwtAuthentication(context, configuration);
            ConfigureLocalization();
            ConfigureVirtualFileSystem(context);
            ConfigureCors(context, configuration);
            ConfigureSwaggerServices(context);
            ConfigureAbpExcepotions(context);
            ConfigureCache(context.Services);
            ConfigureHangfire(context.Services);
            ConfigureAuditLog();
        }

        private void ConfigureJson()
        {
            // 时间格式化
            Configure<AbpJsonOptions>(options => options.DefaultDateTimeFormat = "yyyy-MM-dd HH:mm:ss");
        }
        private void ConfigureAuditLog()
        {
            Configure<AbpAuditingOptions>(options =>
            {
                options.IsEnabled = false; //Disables the auditing system
            });
        }

        private void ConfigureCache(IServiceCollection services)
        {
            var redisConnectionString = services.GetConfiguration().GetSection("Cache:Redis:ConnectionString").Value;
            var redisDatabaseId = Convert.ToInt32(services.GetConfiguration().GetSection("Cache:Redis:DatabaseId").Value);
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString + ",defaultdatabase=" + redisDatabaseId;
            });
        }

        /// <summary>
        /// 注入Hangfire服务
        /// </summary>
        /// <param name="services"></param>
        private void ConfigureHangfire(IServiceCollection services)
        {
            Configure<AbpBackgroundJobOptions>(options =>
            {
                options.IsJobExecutionEnabled = false; 
            });

            var redisConnectionString = services.GetConfiguration().GetSection("Cache:Redis:ConnectionString").Value;
            var redisDatabaseId = Convert.ToInt32(services.GetConfiguration().GetSection("Cache:Redis:DatabaseId").Value);

            // 启用Hangfire 并使用Redis作为持久化
            services.AddHangfire(config =>
            {
                config.UseRedisStorage(redisConnectionString, new RedisStorageOptions { Db = redisDatabaseId });
            });

            JobStorage.Current = new RedisStorage(redisConnectionString, new RedisStorageOptions { Db = redisDatabaseId });

        }

        private void ConfigureAbpExcepotions(ServiceConfigurationContext context)
        {
            // dev环境显示异常具体信息
            if (context.Services.GetHostingEnvironment().IsDevelopment())
            {
                context.Services.Configure<AbpExceptionHandlingOptions>(options =>
                {
                    options.SendExceptionsDetailsToClients = true;
                });
            }
        }

        private void ConfigureOptions(ServiceConfigurationContext context)
        {
            //var configuration = context.Services.GetConfiguration();
            //Configure<JwtOptions>(options =>
            //{
            //    options.Audience = configuration.GetValue<string>("Jwt:Audience");
            //    options.Issuer = configuration.GetValue<string>("Jwt:Audience");
            //    options.SecurityKey = configuration.GetValue<string>("Jwt:SecurityKey");
            //    options.ExpirationTime = configuration.GetValue<int>("Jwt:ExpirationTime");
            //});
            context.Services.Configure<JwtOptions>(context.Services.GetConfiguration().GetSection("Jwt"));
        }
        private void ConfigureBundles()
        {
            Configure<AbpBundlingOptions>(options =>
            {
                options.StyleBundles.Configure(
                    BasicThemeBundles.Styles.Global,
                    bundle => { bundle.AddFiles("/global-styles.css"); }
                );
            });
        }

        private void ConfigureUrls(IConfiguration configuration)
        {
            Configure<AppUrlOptions>(options =>
            {
                options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"];
            });
        }

        private void ConfigureVirtualFileSystem(ServiceConfigurationContext context)
        {
            var hostingEnvironment = context.Services.GetHostingEnvironment();

            if (hostingEnvironment.IsDevelopment())
            {
                Configure<AbpVirtualFileSystemOptions>(options =>
                {
                    options.FileSets.ReplaceEmbeddedByPhysical<ZzzDomainSharedModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $"..{Path.DirectorySeparatorChar}Zzz.Domain.Shared"));
                    options.FileSets.ReplaceEmbeddedByPhysical<ZzzDomainModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $"..{Path.DirectorySeparatorChar}Zzz.Domain"));
                    options.FileSets.ReplaceEmbeddedByPhysical<ZzzApplicationContractsModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $"..{Path.DirectorySeparatorChar}Zzz.Application.Contracts"));
                    options.FileSets.ReplaceEmbeddedByPhysical<ZzzApplicationModule>(
                        Path.Combine(hostingEnvironment.ContentRootPath,
                            $"..{Path.DirectorySeparatorChar}Zzz.Application"));
                });
            }
        }

        private void ConfigureConventionalControllers()
        {
            Configure<AbpAspNetCoreMvcOptions>(options =>
            {
                options.ConventionalControllers.Create(typeof(ZzzApplicationModule).Assembly);
            });
        }

        private void ConfigureJwtAuthentication(ServiceConfigurationContext context, IConfiguration configuration)
        {
            context.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters()
                    {
                        // 是否开启签名认证
                        ValidateIssuerSigningKey = true,
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        //ClockSkew = TimeSpan.Zero,
                        ValidIssuer = configuration["Jwt:Issuer"],
                        ValidAudience = configuration["Jwt:Audience"],
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(configuration["Jwt:SecurityKey"]))
                    };
                });
        }

        private static void ConfigureSwaggerServices(ServiceConfigurationContext context)
        {
            context.Services.AddSwaggerGen(
                options =>
                {
                    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Zzz API", Version = "v1" });
              
                    options.DocInclusionPredicate((docName, description) => true);
                    options.EnableAnnotations();// 启用注解
                    // 加载xml文件，不然不会显示备注
                    var xmlapppath = Path.Combine(AppContext.BaseDirectory, "Zzz.Application.xml");
                    var xmlContractspath = Path.Combine(AppContext.BaseDirectory, "Zzz.Application.Contracts.xml");
                    var xmlapipath = Path.Combine(AppContext.BaseDirectory, "Zzz.HttpApi.Host.xml");
                    options.IncludeXmlComments(xmlapppath, true);
                    options.IncludeXmlComments(xmlContractspath, true);
                    options.IncludeXmlComments(xmlapipath, true);

                    options.OperationFilter<SwaggerTagsFilter>();

                    // 在swaggerui界面添加token认证
                    options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, new OpenApiSecurityScheme()
                    {
                        Description = "Please enter into field the word 'Bearer' followed by a space and the JWT value",
                        Name = "Authorization",
                        In = ParameterLocation.Header,
                        Type = SecuritySchemeType.Http,
                        Scheme = JwtBearerDefaults.AuthenticationScheme,
                        BearerFormat = "JWT"
                    });
                    options.AddSecurityRequirement(new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" 
                            }
                        },
                        new List<string>()
                        }
                    });
                });
        }

        private void ConfigureLocalization()
        {
            Configure<AbpLocalizationOptions>(options =>
            {
                options.Languages.Add(new LanguageInfo("en", "en", "English"));
                options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "简体中文"));
            });
        }

        private void ConfigureCors(ServiceConfigurationContext context, IConfiguration configuration)
        {
            context.Services.AddCors(options =>
            {
                options.AddPolicy(DefaultCorsPolicyName, builder =>
                {
                    builder
                        .WithOrigins(
                            configuration["App:CorsOrigins"]
                                .Split(",", StringSplitOptions.RemoveEmptyEntries)
                                .Select(o => o.RemovePostFix("/"))
                                .ToArray()
                        )
                        .WithAbpExposedHeaders()
                        .SetIsOriginAllowedToAllowWildcardSubdomains()
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });
            });
        }





        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            var app = context.GetApplicationBuilder();
            var env = context.GetEnvironment();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseAbpRequestLocalization();

            if (!env.IsDevelopment())
            {
                app.UseErrorPage();
            }

            app.UseCorrelationId();
            app.UseVirtualFiles();
            app.UseRouting();
            app.UseCors(DefaultCorsPolicyName);
            app.UseAuthentication();
            app.UseJwtTokenMiddleware();

            //if (MultiTenancyConsts.IsEnabled)
            //{
            //    app.UseMultiTenancy();
            //}

            //app.UseIdentityServer();
            app.UseAuthorization();

            app.UseSwagger();
            app.UseAbpSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Zzz API");
                c.DefaultModelExpandDepth(-2);
                c.DocExpansion(DocExpansion.None);
            });

            app.UseAuditing();
            //app.UseAbpSerilogEnrichers();
            app.UseSerilogRequestLogging(opts =>
            {
                opts.EnrichDiagnosticContext = SerilogToEsExtensions.EnrichFromRequest;
            });
            app.UseConfiguredEndpoints();

   
        }
    }
}

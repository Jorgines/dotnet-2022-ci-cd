﻿using Ardalis.ListStartupServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.eShopWeb.Infrastructure.Data;
using Microsoft.eShopWeb.Infrastructure.Identity;
using Microsoft.eShopWeb.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using BlazorAdmin;
using BlazorAdmin.Services;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using BlazorShared;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;

namespace Microsoft.eShopWeb.Web
{
    public class Startup
    {
        private IServiceCollection _services;
        private readonly ILoggerFactory loggerFactory;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureCodespacesServices(IServiceCollection services)
        {
            ConfigureSqlDatabase(services);

            ConfigureServices(services);
        }

        private void ConfigureSqlDatabase(IServiceCollection services)
        {
            services.AddDbContext<CatalogContext>(c =>
                            c.UseSqlServer(Configuration.GetConnectionString("CatalogConnection")));

            // Add Identity DbContext
            services.AddDbContext<AppIdentityDbContext>(options =>
                options.UseSqlServer(Configuration.GetConnectionString("IdentityConnection")));
        }

        public void ConfigureDevelopmentServices(IServiceCollection services)
        {
            // use in-memory database
            //ConfigureInMemoryDatabases(services);

            ConfigureSqlDatabase(services);

            ConfigureServices(services);
        }

        public void ConfigureDockerServices(IServiceCollection services)
        {
            services.AddDataProtection()
                .PersistKeysToAzureBlobStorage(GetBlobClient())
                .SetApplicationName("eshopwebmvc")
                .PersistKeysToFileSystem(new DirectoryInfo(@"./"));

            ConfigureDevelopmentServices(services);
        }

        private BlobClient GetBlobClient()
        {
            var client = new BlobServiceClient(Configuration["DataProtection:StorageConnString"]);

            BlobContainerClient containerClient = client.GetBlobContainerClient(Configuration["DataProtection:Container"]);
            BlobClient blobClient = containerClient.GetBlobClient(Configuration["DataProtection:blobName"]);
            return blobClient;
        }

        private void ConfigureInMemoryDatabases(IServiceCollection services)
        {
            // use in-memory database
            services.AddDbContext<CatalogContext>(c =>
                c.UseInMemoryDatabase("Catalog"));

            // Add Identity DbContext
            services.AddDbContext<AppIdentityDbContext>(options =>
                options.UseInMemoryDatabase("Identity"));

            ConfigureServices(services);
        }

        public void ConfigureProductionServices(IServiceCollection services)
        {
            services.AddDataProtection()
               .PersistKeysToAzureBlobStorage(GetBlobClient())
               .SetApplicationName("eshopwebmvc");

            ConfigureSqlDatabase(services);

            ConfigureServices(services);
        }

        public void ConfigureTestingServices(IServiceCollection services)
        {
            ConfigureInMemoryDatabases(services);
        }


        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCookieSettings();


            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                });

            services.AddIdentity<ApplicationUser, IdentityRole>()
                       .AddDefaultUI()
                       .AddEntityFrameworkStores<AppIdentityDbContext>()
                                       .AddDefaultTokenProviders();

            services.AddScoped<ITokenClaimsService, IdentityTokenClaimService>();

            services.AddCoreServices(Configuration);
            services.AddWebServices(Configuration);

            // Add memory cache services
            services.AddMemoryCache();
            services.AddRouting(options =>
            {
                // Replace the type and the name used to refer to it with your own
                // IOutboundParameterTransformer implementation
                options.ConstraintMap["slugify"] = typeof(SlugifyParameterTransformer);
            });

            // Add Telemetry
            services.AddApplicationInsightsTelemetry();

            services.AddMvc(options =>
            {
                options.Conventions.Add(new RouteTokenTransformerConvention(
                         new SlugifyParameterTransformer()));
                options.Filters.Add(new IgnoreAntiforgeryTokenAttribute());

            });
            services.AddControllersWithViews();
            services.AddRazorPages(options =>
            {
                options.Conventions.AuthorizePage("/Basket/Checkout");
            });
            services.AddHttpContextAccessor();
            services.AddHealthChecks();
            services.Configure<ServiceConfig>(config =>
            {
                config.Services = new List<ServiceDescriptor>(services);

                config.Path = "/allservices";
            });


            var baseUrlConfig = new BaseUrlConfiguration();
            Configuration.Bind(BaseUrlConfiguration.CONFIG_NAME, baseUrlConfig);
            services.AddScoped<BaseUrlConfiguration>(sp => baseUrlConfig);
            // Blazor Admin Required Services for Prerendering
            services.AddScoped<HttpClient>(s => new HttpClient
            {
                BaseAddress = new Uri(baseUrlConfig.WebBase)
            });

            // add blazor services
            services.AddBlazoredLocalStorage();
            services.AddServerSideBlazor();

            services.AddScoped<HttpService>();
            services.AddBlazorServices();

            _services = services; // used to debug registered services
        }


        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseHealthChecks("/health",
                new HealthCheckOptions
                {
                    ResponseWriter = async (context, report) =>
                    {
                        var result = new
                        {
                            status = report.Status.ToString(),
                            errors = report.Entries.Select(e => new
                            {
                                key = e.Key,
                                value = Enum.GetName(typeof(HealthStatus), e.Value.Status)
                            })
                        }.ToJson();
                        context.Response.ContentType = MediaTypeNames.Application.Json;
                        await context.Response.WriteAsync(result);
                    }
                });
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseShowAllServicesMiddleware();
                app.UseDatabaseErrorPage();
                app.UseWebAssemblyDebugging();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseBlazorFrameworkFiles();
            app.UseStaticFiles();
            app.UseRouting();

            app.UseCookiePolicy();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute("default", "{controller:slugify=Home}/{action:slugify=Index}/{id?}");
                endpoints.MapRazorPages();
                endpoints.MapHealthChecks("home_page_health_check");
                endpoints.MapHealthChecks("api_health_check");
                //endpoints.MapBlazorHub("/admin");
                endpoints.MapFallbackToFile("index.html");
            });
        }

    }

}
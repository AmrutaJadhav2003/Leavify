using Rite.LeaveManagement.Svc.Infrastructure.Logging;
using Rite.LeaveManagement.Svc.Middleware; // ADD THIS
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using Rite.LeaveManagement.Svc.Config;
using Rite.LeaveManagement.Svc.Interfaces;
using Rite.LeaveManagement.Svc.Repositories;
using Rite.LeaveManagement.Svc.Services;
using System.Net;

namespace LeaveManagementSystem.API
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            // Inside ConfigureServices
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            services.Configure<MongoDbSettings>(options =>
            {
                options.ConnectionString = Environment.GetEnvironmentVariable("MONGO_CONN_STRING")
                    ?? Configuration["MongoDbSettings:ConnectionString"];


                // _logger.LogInformation("MongoDB Connection String: {ConnectionString}", options.ConnectionString);

                options.DatabaseName = Environment.GetEnvironmentVariable("MONGO_DB_NAME")
                    ?? Configuration["MongoDbSettings:DatabaseName"];
            });

            services.AddSingleton<IMongoClient>(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<MongoDbSettings>>().Value;
                return new MongoClient(settings.ConnectionString);
            });

            services.AddScoped<IMongoDatabase>(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<MongoDbSettings>>().Value;
                var client = sp.GetRequiredService<IMongoClient>();
                return client.GetDatabase(settings.DatabaseName);
            });

            services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
            services.AddScoped<IFcmNotificationService, FcmNotificationService>();
            services.AddScoped<NotificationService>();
            services.AddControllers(o => { o.Filters.Add<ActionLoggingFilter>(); });
            services.AddEndpointsApiExplorer();

            if (FirebaseApp.DefaultInstance == null)
            {
                var serviceAccountPath = Path.Combine(AppContext.BaseDirectory, "Config", "serviceAccountKey.json");

                var firebaseBase64 = Environment.GetEnvironmentVariable("FIREBASE_KEY_B64");
                if (!string.IsNullOrWhiteSpace(firebaseBase64))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(serviceAccountPath));
                    File.WriteAllBytes(serviceAccountPath, Convert.FromBase64String(firebaseBase64));
                }

                FirebaseApp.Create(new AppOptions()
                {
                    Credential = GoogleCredential.FromFile(serviceAccountPath),
                    ProjectId = "leavify-4f966"
                });
            }

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "Leave Management API",
                    Version = "v1"
                });

                // ADD SWAGGER AUTHORIZATION SUPPORT
                c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                {
                    Description = "JWT Authorization header using the Bearer scheme.",
                    Name = "Authorization",
                    In = ParameterLocation.Header,
                    Type = SecuritySchemeType.ApiKey,
                    Scheme = "Bearer"
                });

                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "Bearer"
                            }
                        },
                        Array.Empty<string>()
                    }
                });
            });

            services.AddSingleton<JwtTokenService>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Leave Management API v1");
                c.RoutePrefix = string.Empty;
            });

            var baseStoragePath = Environment.GetEnvironmentVariable("STORAGE_ROOT_PATH")
                     ?? "/data/leavify";

            var leaveDocsPath = Path.Combine(baseStoragePath, "leavedocs");
            var profilePicPath = Path.Combine(baseStoragePath, "profilepics");

            Directory.CreateDirectory(leaveDocsPath);
            Directory.CreateDirectory(profilePicPath);
            //var leaveDocsPath = Path.Combine(Directory.GetCurrentDirectory(), "storage", "leavedocs");

            if (!Directory.Exists(leaveDocsPath))
            {
                Directory.CreateDirectory(leaveDocsPath);
            }

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(leaveDocsPath),
                RequestPath = "/leavedocs"
            });

            //var profilePicPath = Path.Combine(Directory.GetCurrentDirectory(), "storage", "profilepics");

            if (!Directory.Exists(profilePicPath))
            {
                Directory.CreateDirectory(profilePicPath);
            }

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(profilePicPath),
                RequestPath = "/profilepics"
            });

            app.UseRouting();

            // Per-request correlation id scope for all logs
            app.Use(async (context, next) =>
            {
                var corr = context.TraceIdentifier;
                using (logger.BeginScope(new Dictionary<string, object> { { "CorrelationId", corr } }))
                {
                    await next();
                }
            });

            app.UseMiddleware<RequestResponseLoggingMiddleware>();

            // ADD JWT MIDDLEWARE HERE (after logging, before authorization)
            app.UseMiddleware<JwtDatabaseAuthMiddleware>();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
using NLog.Web;

namespace LeaveManagementSystem.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // Boot NLog from nlog.config in basedir (bin/Debug/net8.0)
            var logger = NLogBuilder.ConfigureNLog("nlog.config").GetCurrentClassLogger();

            try
            {
                logger.Debug("init main");
                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Application stopped because of an exception");
                throw;
            }
            finally
            {
                NLog.LogManager.Shutdown();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
           Host.CreateDefaultBuilder(args)
               .ConfigureAppConfiguration((hostingContext, config) =>
               {
                   config.AddEnvironmentVariables(); // keep your ECS env injection
               })
               .ConfigureLogging(logging =>
               {
                   // Remove default providers so NLog is the one writing
                   logging.ClearProviders();
                   // Capture Debug and above (you said for QA you want Debug)
                   logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
               })
               // Plug NLog into ILogger<T>
               .UseNLog()
               .ConfigureWebHostDefaults(webBuilder =>
               {
                   webBuilder.UseStartup<Startup>();
                   webBuilder.ConfigureKestrel((ctx, opts) =>
                   {
                       opts.Configure(ctx.Configuration.GetSection("Kestrel"));
                   });
               });
    }
}

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Rite.LeaveManagement.Svc.Infrastructure.Logging
{
    public class ActionLoggingFilter : IActionFilter, IAsyncActionFilter
    {
        private readonly ILogger<ActionLoggingFilter> _logger;

        public ActionLoggingFilter(ILogger<ActionLoggingFilter> logger)
        {
            _logger = logger;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            _logger.LogInformation("Action start {Controller}.{Action} Args={Args}",
                context.ActionDescriptor.RouteValues["controller"],
                context.ActionDescriptor.RouteValues["action"],
                string.Join(",", context.ActionArguments.Select(kv => kv.Key)));
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
            if (context.Exception != null)
            {
                _logger.LogError(context.Exception, "Action error {Controller}.{Action}",
                    context.ActionDescriptor.RouteValues["controller"],
                    context.ActionDescriptor.RouteValues["action"]);
            }
            else
            {
                _logger.LogInformation("Action end {Controller}.{Action} Status={StatusCode}",
                    context.ActionDescriptor.RouteValues["controller"],
                    context.ActionDescriptor.RouteValues["action"],
                    context.HttpContext.Response?.StatusCode);
            }
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            OnActionExecuting(context);
            var result = await next();
            OnActionExecuted(new ActionExecutedContext(context, context.Filters, context.Controller)
            {
                Exception = result.Exception,
                HttpContext = context.HttpContext
            });
        }
    }
}

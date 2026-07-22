using Microsoft.Extensions.Logging;
using ZLogger;

namespace ConsoleApp;

public partial class BusinessLogic(ILogger<BusinessLogic> logger)
{
    [ZLoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Source Generator: Action {actionName} started. Iterations: {count}")]
    private partial void LogActionStarted(ILogger<BusinessLogic> logger, string actionName, int count);

    [LoggerMessage(EventId = 101, Level = LogLevel.Debug, Message = "Source Generator: Processing iteration {iteration} of {total} for {actionName}")]
    private partial void LogProcessing(int iteration, int total, string actionName);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Source Generator: Action {actionName} completed successfully.")]
    private partial void LogActionCompleted(string actionName);

    public void PerformAction(string actionName, int count)
    {
        logger.ZLogInformation($"Action {actionName} started. Iterations: {count}");
        LogActionStarted(logger, actionName, count);
        
        for (var i = 0; i < count; i++)
        {
            logger.ZLogDebug($"Processing iteration {i + 1:@iteration} of {count} for {actionName}");
            LogProcessing(i + 1, count, actionName);
        }

        logger.ZLogInformation($"Action {actionName} completed successfully.");
        LogActionCompleted(actionName);
    }

    public void HandleError(string operation, Exception ex)
    {
        logger.ZLogError(ex, $"An error occurred during {operation}");
    }
}

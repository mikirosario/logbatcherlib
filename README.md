# logbatcherlib: Advanced Logging for .NET Applications

Elevate your .NET Framework 4.7+ applications with `logbatcherlib`, a comprehensive logging library designed for versatility, reliability, and performance. This thread-safe library empowers developers with a suite of robust logging functionalities encapsulated in the `Logger` class.

## Key Features of logbatcherlib:

1. **Thread-Safe Logging**: Built with concurrency in mind, `logbatcherlib` ensures safe and consistent logging across multi-threaded environments, safeguarding your log data integrity at all times.

2. **Flexible Log Management**: Tailor your logging strategy with customizable settings, including `MaxFileSizeInBytes`, `TotalLogFiles`, and `LogDumpFrequency`. Whether it’s fine-grained control over log file size or managing the number of log files, `logbatcherlib` adapts to your specific requirements.

3. **Sophisticated File Handling**: Experience seamless log file rollovers with temporary file creation and sophisticated file management. This feature ensures continuous, organized logging, and efficient disk space management.

4. **Rich Logging Options**: Whether logging simple text messages or serializing complex C# objects into JSON, `logbatcherlib` handles it all. Customize JSON serialization to include or exclude null and default values to save disk space, providing flexibility in how you log and store data.

5. **Intelligent Log Retrieval and Deletion**: Easily retrieve and read log entries, with an option to delete them post-retrieval. This functionality aids in efficient log analysis and disk space management.

6. **Robust Error Handling and State Management**: With comprehensive error handling and clear state reporting (`LoggerState`), `logbatcherlib` offers reliability and transparency in its operation, ensuring you're always informed of the logger's status.

7. **Mutex-Based File Access Control**: Protect your log files in multi-threaded scenarios with mutex-based synchronization, ensuring safe and exclusive access to log files, thereby preventing data corruption.

8. **Automatic Log Rollback**: Maintain a consistent and uninterrupted sequence of log files with the rollback functionality, even after deletions, ensuring your logs are always organized and traceable.

9. **Convenience Methods**: Utility methods like `AffixTimestamp` and `SanitizeLogString` enhance logging convenience, adding timestamps and ensuring clean log strings.

10. **Exception Resilience**: Designed to gracefully handle various file-related exceptions, `logbatcherlib` provides a stable and reliable logging experience, even in the face of unexpected file system errors.

## Integrate logbatcherlib Into Your Workflow:

`logbatcherlib` is designed for flexibility, allowing you to use it for scoped logging within a `using` block or for persistent logging throughout your application's lifecycle. Its advanced features cater to diverse logging needs, from short-term debugging to long-term application monitoring.

## In Conclusion:

`logbatcherlib` stands as an elite choice for .NET developers seeking a powerful, customizable, and dependable logging solution. Its rich feature set and thoughtful design make it a valuable tool for any .NET application requiring sophisticated log management.

## Requirements
- .NET Framework 4.7+
- msbuild version 16.10.1+

## Building the Project
Open a terminal and cd into the project directory, then copy and paste these lines:

**Restore dependencies and build the project**:
```console
msbuild /property:GenerateFullPaths=true /t:restore /p:RestorePackagesConfig=true
msbuild /property:GenerateFullPaths=true /property:Configuration=Release /t:build
```

## Importing the Library into a .NET Framework Project

To integrate `logbatcherlib` into your .NET Framework project, add the following lines to your `.csproj` file.

**Add Project Reference**:
   ```xml
   <ItemGroup>
       <ProjectReference Include="[Path]/[To]/[LogBatcherLib]/[Directory]/*.csproj" />
   </ItemGroup>
   ```

## Usage Example
```csharp
using (Logger myLogger = new Logger("McLogface", "/home/MyUser/Documents/MyLogs/testLog", 10))
{
    MyData data = new MyData();
    myLogger.Log(Logger.AffixTimestamp($"{myLogger.LoggerName} logged my data."));
    myLogger.LogJsonConsole(data);
    myLogger.LogJsonIgnoreDefaults(data);
}
```
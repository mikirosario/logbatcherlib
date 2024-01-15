using System;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Collections;

namespace LogBatcher
{
    public class Logger : IDisposable
    {
        private enum Signal
        {
            LOG,
            STOP
        }
        public enum LoggerState
        {
            UNSTARTED,
            ACTIVE,
            FINISHED_ERROR,
            FINISHED_SUCCESS
        }
        public enum JsonExclusionOptions
        {
            INCLUDE_ALL,
            EXCLUDE_NULL_VALUES,
            EXCLUDE_DEFAULT_VALUES,
        }
        public enum LogFileType
        {
            PERSISTENT,
            TEMPORARY
        }
        private const string ConsoleMessagePrefix = "[LogBatcher] ";
        private const string LoggerNameDefault = "LogBatcher";
        private const string LoggerFileNameDefault = "log";
        private const string LoggerFileNameExtension = ".log";
        private const string LoggerTmpFileNameExtension = LoggerFileNameExtension + ".tmp";
        private const byte LoggerNameMaxLength = 50;
        private const byte LogFilePathMaxLength = 200; // Should not be greater than 260 for cross-platform compatibility
        private const byte TotalLogFilesLowerLimit = 1;
        private const long FileSizeUpperLimit = 524288000;
        private const ushort LogDumpFrequencyLowerLimit = 1;
        private readonly object _internalFileLock = new object();
        private readonly JsonSerializerOptions _jsonSerializerConfigIncludeAllPretty = new JsonSerializerOptions() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        private readonly JsonSerializerOptions _jsonSerializerConfigIgnoreDefaultValuesPretty = new JsonSerializerOptions() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
        private readonly JsonSerializerOptions _jsonSerializerConfigIgnoreNullValuesPretty = new JsonSerializerOptions() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        private readonly JsonSerializerOptions _jsonSerializerConfigIncludeAll = new JsonSerializerOptions() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
        private readonly JsonSerializerOptions _jsonSerializerConfigIgnoreDefaultValues = new JsonSerializerOptions() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
        private readonly JsonSerializerOptions _jsonSerializerConfigIgnoreNullValues = new JsonSerializerOptions() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        private readonly ConcurrentQueue<string> _logEntries = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _logSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent _stopSignal = new AutoResetEvent(false);
        private readonly LinkedList<Mutex> _mutexList = new LinkedList<Mutex>();
        private readonly string _loggerName;
        private readonly LogFilePath[] _allPossiblePersistentLogFilePaths;
        private readonly LogFilePath[] _allPossibleTemporaryLogFilePaths;
        private readonly string[] _allPossibleFilePathsAsMutexNames;
        private readonly LogFilePath _baseLogFilePathNoExtension;
        private readonly LogFilePath _baseLogFilePathWithExtension;
        private readonly LogFilePath _baseLogTmpFilePathWithExtension;
        private long _baseLogFileSize = 0;
        private long _maxFileSizeInBytes;
        private ushort _logDumpFrequency;
        private byte _totalLogFiles;
        private bool _disposed = false;
        private volatile bool _errorFlag = false;
        private volatile bool _stop = false;
        private readonly Thread _loggingThread;

        private static Encoding Encoder { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private IEnumerable<LogFilePath> AllExistingPersistentLogFilePaths { get => GetAllExistingPersistentLogFilePaths(); }
        public IEnumerable<string> AllLogs { get => ReadAllLogs(); }
        public LoggerState State { get => GetLoggerState(); }
        public string LoggerName { get => _loggerName; }
        public long MaxFileSizeInBytes
        {
            get => _maxFileSizeInBytes;
            private set => _maxFileSizeInBytes = Utils.Clamp(value, 0, FileSizeUpperLimit);
        }
        public byte TotalLogFiles
        {
            get => _totalLogFiles;
            private set => _totalLogFiles = Math.Max(TotalLogFilesLowerLimit, value);
        }
        public ushort LogDumpFrequency
        {
            get => _logDumpFrequency;
            private set => _logDumpFrequency = Math.Max(LogDumpFrequencyLowerLimit, value);
        }

        public Logger(string loggerName, string logFilePath, ushort logDumpFrequency, long maxLogFileSizeInBytes = FileSizeUpperLimit, byte totalLogFiles = byte.MaxValue)
        {
            try
            {
                MaxFileSizeInBytes = maxLogFileSizeInBytes;
                LogDumpFrequency = logDumpFrequency;
                TotalLogFiles = totalLogFiles;
                _loggerName = ValidateLoggerName(loggerName);
                _baseLogFilePathNoExtension = ValidatePathAndCreateDirectory(logFilePath);
                _allPossiblePersistentLogFilePaths = GenerateAllPossiblePersistentLogFilePaths();
                _allPossibleTemporaryLogFilePaths = GenerateAllPossibleTmpLogFilePaths();
                _allPossibleFilePathsAsMutexNames = GenerateAllPossibleFilePathsAsMutexNames();
                _baseLogFilePathWithExtension = _allPossiblePersistentLogFilePaths[0];
                _baseLogTmpFilePathWithExtension = _allPossibleTemporaryLogFilePaths[0];
                _baseLogFileSize = QueryFileSystemForBaseLogFileSize();
                _loggingThread = new Thread(ThreadLogQueueMonitor);
                _loggingThread.IsBackground = true;
                _loggingThread.Start();
            }
            catch (Exception ex)
            {
                PrintError(ex.Message);
            }
        }

        /* DISPOSE PATTERN */
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Stop();
                    _logSignal?.Dispose();
                    _stopSignal?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Affixes a timestamp indicating the system date and time to your message in the
        /// following format:
        /// "1970/01/31 15:30:45 | [message]".
        /// </summary>
        public static string AffixTimestamp(string message)
        {
            return $"{DateTime.Now:yyyyMMddHHmmss} | {SanitizeLogString(message)}";
        }

        public void Stop()
        {
            if (!_stop)
            {
                _stop = true;
                _stopSignal.Set(); // Signal the logger thread to stop
                _loggingThread?.Join(); // Wait for the logger thread to finish
                PrintInfo($"{LoggerName} stopped");
            }
        }

        /// <summary>
        /// Logs a message to file.
        /// </summary>
        public void Log(string message)
        {
            _logEntries.Enqueue(SanitizeLogString(message));
            if (_logEntries.Count >= LogDumpFrequency)
            {
                _logSignal.Set();
            }
        }

        /// <summary>
        /// Logs a message to console.
        /// </summary>
        public void LogConsole(string message)
        {
            Console.WriteLine($"{LoggerName} logged: {SanitizeLogString(message)}");
        }

        /// <summary>
        /// Logs an object to file as a serialized, newline-delimited JSON string. The 'option'
        /// parameter can be passed to ignore any properties in the object set to their default
        /// values or set to null and thus compress the size of the log. By default, all properties
        /// are included.
        /// </summary>
        /// <remarks>
        /// Note that this method makes a run-time decision, which does involve some additional CPU
        /// overhead. If the option is known at compile-time, the option-specific methods
        /// (LogJsonIncludeAll, etc.) can be called directly.
        /// </remarks>
        public void LogJson(object obj, JsonExclusionOptions option = JsonExclusionOptions.INCLUDE_ALL)
        {
            switch (option)
            {
                case JsonExclusionOptions.EXCLUDE_NULL_VALUES:
                    LogJsonIgnoreNulls(obj);
                    break;
                case JsonExclusionOptions.EXCLUDE_DEFAULT_VALUES:
                    LogJsonIgnoreDefaults(obj);
                    break;
                default:
                    LogJsonIncludeAll(obj);
                    break;
            }
        }

        /// <summary>
        /// Logs an object to console as a JSON string.
        /// </summary>
        /// <remarks>
        /// The 'exclusionOption' parameter can be passed to exclude any properties in the object
        /// that are set to their default values or to null. The 'unescape' parameter can be set to
        /// false to get valid JSON output or to true to resolve any escaped character sequences
        /// into human-readable characters. The 'prettyPrint' parameter can be set to false to have
        /// the JSON displayed in a single line or to true to have it displayed in multiple lines.
        /// By default, all properties are included, escaped characters are made human-readable and
        /// the output is pretty-printed.
        /// </remarks>
        public void LogJsonConsole(object obj, JsonExclusionOptions exclusionOption = JsonExclusionOptions.INCLUDE_ALL, bool unescape = true, bool prettyPrint = true)
        {
            JsonSerializerOptions[] jsonOptionsArray = new JsonSerializerOptions[3];
            switch (prettyPrint)
            {
                case true:
                    jsonOptionsArray[0] = _jsonSerializerConfigIgnoreDefaultValuesPretty;
                    jsonOptionsArray[1] = _jsonSerializerConfigIgnoreNullValuesPretty;
                    jsonOptionsArray[2] = _jsonSerializerConfigIncludeAllPretty;
                    break;
                default:
                    jsonOptionsArray[0] = _jsonSerializerConfigIgnoreDefaultValues;
                    jsonOptionsArray[1] = _jsonSerializerConfigIgnoreNullValues;
                    jsonOptionsArray[2] = _jsonSerializerConfigIncludeAll;
                    break;
            }
            string stringifiedJson;
            switch (exclusionOption)
            {
                case JsonExclusionOptions.EXCLUDE_DEFAULT_VALUES:
                    stringifiedJson = SerializeToJson(obj, jsonOptionsArray[0]);
                    break;
                case JsonExclusionOptions.EXCLUDE_NULL_VALUES:
                    stringifiedJson = SerializeToJson(obj, jsonOptionsArray[1]);
                    break;
                default:
                    stringifiedJson = SerializeToJson(obj, jsonOptionsArray[2]);
                    break;
            }
            if (unescape == true)
                stringifiedJson = Regex.Unescape(stringifiedJson);
            LogConsole(stringifiedJson);
        }

        /// <summary>
        /// Logs an object to file as a serialized, newline-delimited JSON string, including all
        /// properties.
        /// </summary>
        public void LogJsonIncludeAll(object obj)
        {
            Log(SerializeToJson(obj, _jsonSerializerConfigIncludeAll));
        }

        /// <summary>
        /// Logs an object to file as a serialized, newline-delimited JSON string, excluding all
        /// properties set to their default values.
        /// </summary>
        public void LogJsonIgnoreDefaults(object obj)
        {
            Log(SerializeToJson(obj, _jsonSerializerConfigIgnoreDefaultValues));
        }

        /// <summary>
        /// Logs an object to file as a serialized, newline-delimited JSON string, excluding all
        /// properties set to null.
        /// </summary>
        public void LogJsonIgnoreNulls(object obj)
        {
            Log(SerializeToJson(obj, _jsonSerializerConfigIgnoreNullValues));
        }

        /// <summary>
        /// Returns a collection containing every log saved to disc, starting with the most recent.
        /// </summary>
        /// <remarks>
        /// While iterating this collection, Logger will stop writing new logs.
        /// </remarks>
        public IEnumerable<string> ReadAllLogs()
        {
            lock (_internalFileLock)
            {
                foreach (LogFilePath filePath in AllExistingPersistentLogFilePaths)
                {
                    foreach (string line in File.ReadLines(filePath))
                    {
                        yield return line;
                    }
                }
            }
        }

        /// <summary>
        /// Returns a collection containing every log saved to disc, starting with the most recent,
        /// and deletes them from the disc.
        /// </summary>
        /// <remarks>
        /// While iterating this collection, Logger will stop writing new logs.
        /// </remarks>
        public IEnumerable<string> ReadAndDeleteAllLogs()
        {            
            lock (_internalFileLock)
            {
                foreach (LogFilePath filePath in AllExistingPersistentLogFilePaths)
                {
                    foreach (string line in File.ReadLines(filePath))
                    {
                        yield return line;
                    }
                }
                DeleteAllLogFiles();
                _baseLogFileSize = 0;
            }
        }

        //TODO: It would be more ideal to separate this into a ReadJsonLogs and a generic static DeserializeJsonLog, but I'd need a reliable way to verify the JSONness of each log without calling Deserialize
        /// <summary>
        /// Returns a collection containing every JSON log saved to disc deserialized and converted
        /// into type 'T', starting with the most recent, and deletes them from the disc.
        /// </summary>
        /// <remarks>
        /// While iterating this collection, Logger will stop writing new logs.
        /// </remarks>
        public IEnumerable<T> ReadAndDeleteAllDeserializedJsonLogs<T>()
        {
            lock (_internalFileLock)
            {
                List<LogFileDeleteInfo> logsNotToDelete = new List<LogFileDeleteInfo>(TotalLogFiles);
                foreach (LogFilePath filePath in AllExistingPersistentLogFilePaths)
                {
                    LogFileDeleteInfo logFileDeleteInfo = new LogFileDeleteInfo(filePath.Index);
                    ulong logLineNumberInFile = 0;
                    foreach (string line in File.ReadLines(filePath))
                    {
                        if (TryDeserializeJson(line, out T deserializedJson))
                        {
                            yield return deserializedJson;
                        }
                        else
                        {
                            logFileDeleteInfo.LogsToKeep.AddLast(logLineNumberInFile);
                        }
                        ++logLineNumberInFile;
                    }
                    logFileDeleteInfo.TotalLogsInFile = logLineNumberInFile;
                    logsNotToDelete.Add(logFileDeleteInfo);
                }
                DeleteLogsFromFiles(logsNotToDelete);
                _baseLogFileSize = QueryFileSystemForBaseLogFileSize();
            }
        }

        public IEnumerable<T> ReadAllDeserializedJsonLogs<T>()
        {
            lock (_internalFileLock)
            {
                foreach (LogFilePath filePath in AllExistingPersistentLogFilePaths)
                {
                    foreach (string line in File.ReadLines(filePath))
                    {
                        if (TryDeserializeJson(line, out T deserializedJson))
                        {                    
                            yield return deserializedJson;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Serializes an object as a JSON string, applying the JsonSerializerOptions passed in the
        /// 'options' parameter, or the default options if none is passed.
        /// </summary>
        /// <remarks>
        /// JsonSerializerOptions depends on the System.Text.Json library.
        /// </remarks>
        public static string SerializeToJson(object obj, JsonSerializerOptions options = null)
        {
            return JsonSerializer.Serialize(obj, options);
        }

        private static string SanitizeLogString(string logString)
        {
            return logString.Trim('\0');
        }

        private static void PrintError(string errorMsg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(GenerateConsoleMessage(errorMsg));
            Console.ResetColor();
        }

        private static void PrintWarning(string warningMsg)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine(GenerateConsoleMessage(warningMsg));
            Console.ResetColor();
        }

        private static void PrintInfo(string infoMsg)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(GenerateConsoleMessage(infoMsg));
            Console.ResetColor();
        }

        private string ValidateLoggerName(string loggerName)
        {
            string returnValue;
            if (loggerName.Length > LoggerNameMaxLength)
            {
                returnValue = loggerName.Substring(0, LoggerNameMaxLength);
                PrintError($"Invalid Logger Name: {loggerName}.");
                PrintWarning($"Logger Name too long (+{LoggerNameMaxLength} characters). Truncated to: {returnValue}.");
            }
            else
            {
                returnValue = loggerName;
            }
            if (string.IsNullOrWhiteSpace(returnValue) || loggerName.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                returnValue = LoggerNameDefault;
                PrintError($"Invalid Logger Name: {loggerName}.");
                PrintWarning($"Logger Name contains invalid characters. Using default Logger Name: {LoggerNameDefault}.");
            }
            return returnValue;
        }

        private static int CountDirectoriesInPath(string path)
        {
           if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
           {
                return -1;
           }
           int dirCount = 0;
           foreach (char c in path)
           {
                if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                {
                    ++dirCount;
                }
            }
           return dirCount;
        }

        private static string CleanPath(string path)
        {
            char dirSep = Path.DirectorySeparatorChar;
            char altDirSep = Path.AltDirectorySeparatorChar;
            string pattern = $"[{Regex.Escape(dirSep.ToString())}{Regex.Escape(altDirSep.ToString())}]+";
            string replacement = dirSep.ToString();
            string cleanedPath = Regex.Replace(path, pattern, replacement);

            return cleanedPath;
        }

        private static string ConvertFilePathToMutexName(string filePath)
        {
            StringBuilder mutexNameBuilder = new StringBuilder("logbatcherlib-");
            string sanitizedPathName = filePath.Replace($@"{Path.DirectorySeparatorChar}", string.Empty).Replace($@"{Path.AltDirectorySeparatorChar}", string.Empty);

            mutexNameBuilder.Append(sanitizedPathName);

            return mutexNameBuilder.ToString();
        }

        private static string GenerateConsoleMessage(string message)
        {
            return ConsoleMessagePrefix + message;
        }

        private LoggerState GetLoggerState()
        {
            if (_loggingThread == null || _loggingThread.ThreadState == ThreadState.Unstarted )
                return LoggerState.UNSTARTED;
            if (_loggingThread.IsAlive)
                return LoggerState.ACTIVE;
            if (_errorFlag)
                return LoggerState.FINISHED_ERROR;
            return LoggerState.FINISHED_SUCCESS;            
        }

        /// <summary>
        /// Validates <paramref name="logFilePath"/>. Returns validated absolute
        /// log file path to the base (active) log file minus its file extension.
        /// </summary>
        /// <remarks>
        /// Relative paths will be placed in the executable's directory and all
        /// directories therein will be created if necessary.
        /// 
        /// Absolute paths must reference pre-existing directories, except for
        /// the last directory in the path, which will be created if necessary.
        /// 
        /// Paths without directory information will be used as file names in
        /// the fallback directory.
        /// 
        /// Invalid file paths result in the use of the fallback path
        /// LoggerName/LoggerFileNameDefault being used as the log file name.
        /// The fallback path will be placed in the executable's directory.
        /// 
        /// Throws all Directory.CreateDirectory and Path.Combine exceptions
        /// when calls to these method from within the catch block fail. Other
        /// exceptions are handled and cause the fallback path to be used.
        /// </remarks>
        /// <param name="logFilePath">Desired path to the base log file.</param>
        /// <returns>Validated absolute path to the base log file.</returns>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="PathTooLongException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="NotSupportedException"></exception>
        private LogFilePath ValidatePathAndCreateDirectory(string logFilePath)
        {
            string baseLogPath;
            string exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            try
            {
                if (string.IsNullOrWhiteSpace(logFilePath))
                {
                    throw new ArgumentException($"Invalid {LoggerName} Log File Path. No path provided.");
                }
                if (logFilePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    throw new ArgumentException($"Invalid {LoggerName} Log File Path: {logFilePath}. Invalid characters.");
                }
                if (logFilePath.Length > LogFilePathMaxLength)
                {
                    throw new PathTooLongException($"Invalid {LoggerName} Log File Path: {logFilePath}. Too long (+{LogFilePathMaxLength} characters).");
                }

                string cleanPath = CleanPath(logFilePath);
                int dirCount = CountDirectoriesInPath(cleanPath);
                string file = Path.GetFileName(cleanPath);
                bool hasFile = !string.IsNullOrWhiteSpace(file);
                string dir;

                if (dirCount == 0) //Only file info provided
                {
                    dir = Path.Combine(exeDirectory, LoggerName);
                }
                else //path contains directory information
                {

                    //For rooted paths, if only root was specified (no file
                    //information) it's combined with a fallback file value.
                    //Otherwise check to make sure the parent directory of the
                    //last directory in the path exists.
                    if (Path.IsPathRooted(cleanPath))
                    {
                        string parentDir = null;
                        if (!hasFile) //only directory info provided
                        {
                            dir = cleanPath;
                            file = LoggerFileNameDefault;
                        }
                        else //root directory info + file info provided
                        {
                            dir = Path.GetDirectoryName(cleanPath);
                            parentDir = Path.GetDirectoryName(dir);
                            if (parentDir != null && !Directory.Exists(parentDir)) //parent directories of last absolute directory must already be created
                            {
                                throw new DirectoryNotFoundException($"Parent directory {parentDir} does not exist.");
                            }
                        }
                    }
                    else //relative paths
                    {
                        string relativeDirectory = Path.GetDirectoryName(logFilePath);
                        if (!hasFile) //only directory info provided
                        {
                            file = LoggerFileNameDefault;
                        }
                        dir = Path.Combine(exeDirectory, relativeDirectory);
                    }
                }
                Directory.CreateDirectory(dir);
                baseLogPath = Path.Combine(dir, file);
            }
            catch (Exception ex)
            {
                string fallbackDir = Path.Combine(exeDirectory, LoggerName);
                string fallbackFile = LoggerFileNameDefault;
                Directory.CreateDirectory(fallbackDir);
                baseLogPath = Path.Combine(fallbackDir, fallbackFile);
                PrintError($"{LoggerName} Path Validation Error: {logFilePath}{Environment.NewLine}{ex.Message}");
                PrintWarning($"{LoggerName} using default directory path {fallbackDir}");
            }
            return new LogFilePath(baseLogPath, 0);
        }

        private long QueryFileSystemForBaseLogFileSize()
        {
            return File.Exists(_baseLogFilePathWithExtension) ? new FileInfo(_baseLogFilePathWithExtension).Length : 0;
        }

        /// <summary>
        /// Waits for a log signal or a stop signal.
        /// Returns the signal type received.
        /// </summary>
        /// <remarks>
        /// Thread-safe event handler used in the thread running
        /// ThreadWriteLogEntries to signal the thread to write all queued logs
        /// to file (Signal.LOG) or to stop the thread (Signal.STOP).
        /// </remarks>
        /// <returns>
        /// A Signal enum indicating the signal type received.
        /// </returns>
        private Signal ThreadWaitSignal()
        {
            int signalIndex = WaitHandle.WaitAny(new[] { _logSignal, _stopSignal }); //Wait for any signal
            switch (signalIndex)
            {
                case (int)Signal.STOP:
                    return Signal.STOP;
                default:
                    return Signal.LOG;
            }
        }

        private void ThreadLogQueueMonitor()
        {
            bool mutexesAreOwned = false;
            try
            {
                mutexesAreOwned = TryGetMutexes(_allPossibleFilePathsAsMutexNames);
                if (!mutexesAreOwned)
                {
                    throw new FileAccessConflictException("It appears that another thread or process is accessing these files. Close it or change the file path.");
                }
                while (!_stop && ThreadWaitSignal() != Signal.STOP)
                {
                    ThreadWriteAllLogsToFile();
                }
                PrintInfo($"{LoggerName} saving cached logs to file and closing...");
                ThreadWriteAllLogsToFile();
            }
            catch (Exception ex)
            {
                _errorFlag = true;
                PrintError($"{LoggerName} Error in ThreadLogQueueMonitor: {ex.Message}");
            }
            finally
            {
                ClearAllMutexes(mutexesAreOwned);
            }
        }

        private void ThreadWriteAllLogsToFile()
        {
            while (_logEntries.TryDequeue(out string logEntry))
            {
                try
                {
                    logEntry += Environment.NewLine;
                    int logEntrySize = Encoder.GetByteCount(logEntry);
                    if (logEntrySize > MaxFileSizeInBytes)
                    {
                        throw new InvalidOperationException($"Log Entry could " +
                        	"not be written to file. Log entry size " +
                        	$"{logEntrySize} bytes is greater than the Maximum " +
                        	$"File Size {MaxFileSizeInBytes} bytes." +
                        	$"{Environment.NewLine}{logEntry}");
                    }
                    lock (_internalFileLock)
                    {
                        if (IsFullBaseLogFile(logEntrySize))
                        {
                            RollFiles();
                            _baseLogFileSize = QueryFileSystemForBaseLogFileSize();
                        }
                        File.AppendAllText(_baseLogFilePathWithExtension, logEntry, Encoder);
                        _baseLogFileSize += logEntrySize;
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"{LoggerName} Logging error: {ex.Message}");
                }
            }
        }

        private bool IsFullBaseLogFile(int newLogEntrySizeInBytes)
        {
            return _baseLogFileSize + newLogEntrySizeInBytes > MaxFileSizeInBytes;
        }

        /// <summary>
        /// Tries to perform a log file rollover via copy, operate on copy,
        /// overwrite original and delete copy. Should fail safely (without data
        /// loss). In case TotalLogFiles is 1 or less, the existing base log
        /// file content will simply be cleared.
        /// </summary>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="PathTooLongException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="System.Security.SecurityException"></exception>
        /// <exception cref="NotSupportedException"></exception>
        private void RollFiles()
        {
            // Special case for a single log file
            // Simply ovewrite existing file with an empty one
            // TotalLogFiles value of 0 treated as a 1.
            if (TotalLogFiles < 2)
            {
                File.WriteAllText(_baseLogFilePathWithExtension, string.Empty);
                return;
            }

            CreateTmpLogFiles();
            RolloverTmpLogFiles();
            DeletePersistentLogFiles();
            MoveTmpLogFilesToPersistentLogFiles();
            DeleteTmpLogFiles();
        }

        /// <summary>
        /// Tries to perform a backwards log file rollover to fill in any gaps
        /// between log files caused by file deletions.
        /// </summary>
        private void RollbackFiles()
        {
            BitArray file = new BitArray(TotalLogFiles);
            int GetNextRollbackPos(int lastRollbackPos, int filePos) { for (int i = lastRollbackPos + 1; i < filePos; ++i) { if (!file[i]) { return i; } } return -1; }
            for (int i = 0; i < file.Count; ++i)
            {
                file[i] = File.Exists(_allPossiblePersistentLogFilePaths[i]);
            }
            
            int rollbackPos = -1;
            for (int i = 0; i < file.Count; ++i)
            {
                if (file[i] && (rollbackPos = GetNextRollbackPos(rollbackPos, i)) > -1)
                {
                    File.Move(_allPossiblePersistentLogFilePaths[i], _allPossiblePersistentLogFilePaths[rollbackPos]);
                    file[i] = false;
                    file[rollbackPos] = true;
                }
            }
        }

        /// <summary>
        /// Tries to copy all existing log files to a series of corresponding temporary log files.
        /// Throws an exception if unsuccessful for any reason.
        /// </summary>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="PathTooLongException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="System.Security.SecurityException"></exception>
        /// <exception cref="NotSupportedException"></exception>
        private void CreateTmpLogFiles()
        {
            //Copy log files to .tmp files for safe rollover
            try
            {
                for (int i = 0; i < TotalLogFiles; ++i)
                {
                    string originalFile = _allPossiblePersistentLogFilePaths[i];
                    string tmpFile = _allPossibleTemporaryLogFilePaths[i];
                    if (File.Exists(originalFile))
                    {
                        File.Copy(originalFile, tmpFile, overwrite: true);
                    }
                }
            }
            catch
            {
                DeleteTmpLogFiles();
                throw;
            }
        }

        /// <summary>
        /// Tries to move the temporary log files up one slot, such that
        /// base_log_file.tmp becomes log_file.0.tmp, log_file.0.tmp becomes
        /// log_file.1.tmp, etc., until the total number of temporary log files
        /// equals TotalLogFiles. This method will do nothing if TotalLogFiles
        /// is less than 2.
        /// Throws an exception if unsuccessful for any reason.
        /// </summary>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="PathTooLongException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="System.Security.SecurityException"></exception>
        private void RolloverTmpLogFiles()
        {
            try
            {
                if (TotalLogFiles < 2)
                {
                    return;
                }
                int highestLogFileIndex = TotalLogFiles - 1; //-1 for counting from 0
                string lastPossibleFilePath = _allPossibleTemporaryLogFilePaths[highestLogFileIndex];
                if (File.Exists(lastPossibleFilePath))
                {
                    File.Delete(lastPossibleFilePath);
                }
                for (int logFileIndex = highestLogFileIndex - 1; logFileIndex >= 0; --logFileIndex)
                {
                    string targetFile = _allPossibleTemporaryLogFilePaths[logFileIndex];
                    string rollToFile = _allPossibleTemporaryLogFilePaths[logFileIndex + 1];

                    if (File.Exists(targetFile))
                    {
                        if (File.Exists(rollToFile))
                        {
                            File.Delete(rollToFile);
                        }
                        File.Move(targetFile, rollToFile);
                    }
                }

                File.WriteAllText(_allPossibleTemporaryLogFilePaths[0], string.Empty, Encoder);
            }
            catch
            {
                DeleteTmpLogFiles();
                throw;
            }
        }

        /// <summary>
        /// Tries to delete all temporary log files. Throws an exception if
        /// unsuccessful for any reason.
        /// </summary>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="System.Security.SecurityException"></exception>
        private void DeleteTmpLogFiles()
        {
            foreach (string tmpFile in _allPossibleTemporaryLogFilePaths)
            {
                if (File.Exists(tmpFile))
                {
                    File.Delete(tmpFile);
                }
            }
        }

        /// <summary>
        /// Tries to delete all persistent log files. Throws an exception if
        /// unsuccessful for any reason.
        /// </summary>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="System.Security.SecurityException"></exception>
        private void DeletePersistentLogFiles()
        {
            foreach (string logFile in _allPossiblePersistentLogFilePaths)
            {
                if (File.Exists(logFile))
                {
                    File.Delete(logFile);
                }
            }
        }

        /// <summary>
        /// Moves the tmp log files to persistent log files, otherwise throws an
        /// exception.
        /// </summary>
        ///
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="PathTooLongException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="System.Security.SecurityException"></exception>
        private void MoveTmpLogFilesToPersistentLogFiles()
        {
            //Try to move tmp log files to persistent log files
            for (int i = 0; i < TotalLogFiles; ++i)
            {
                string tmpFile = _allPossibleTemporaryLogFilePaths[i];
                string persistentFile = _allPossiblePersistentLogFilePaths[i];
                if (File.Exists(tmpFile))
                {
                    File.Move(tmpFile, persistentFile);
                }
            }
        }

        /* MUTEX HANDLING */
        /// <summary>
        /// Attempts to lock all the named mutexes referenced in the 'mutexNames' array. Returns a
        /// bool indicating whether or not the attempt was successful.
        /// </summary>
        private bool TryGetMutexes(IEnumerable<string> mutexNames)
        {
            if (mutexNames?.Count() < 1)
            {
                throw new ArgumentNullException(nameof(mutexNames));
            }
            bool gotMutexes = false;
            try
            {
                foreach (string mutexName in mutexNames)
                {
                    Mutex mutex = new Mutex(false, mutexName);
                    gotMutexes = mutex.WaitOne(0);
                    if (!gotMutexes)
                    {
                        break;
                    }
                    _mutexList.AddLast(mutex);
                }
            }
            finally
            {
                if (!gotMutexes && _mutexList.Count > 0)
                {
                    ClearAllMutexes(false);
                }
            }
            return gotMutexes;
        }

        /// <summary>
        /// Disposes and clears all mutexes in the mutex list.
        /// If 'mutexesAreOwned' is true, the mutexes will be released before disposal.
        private void ClearAllMutexes(bool mutexesAreOwned)
        {
            void DisposeMutex(Mutex mutex)
            {
                mutex.Dispose();
            }
            void ReleaseAndDisposeMutex(Mutex mutex)
            {
                mutex.ReleaseMutex();
                DisposeMutex(mutex);
            }
            Action<Mutex> disposalAction = mutexesAreOwned ? (Action<Mutex>)ReleaseAndDisposeMutex : DisposeMutex;            
            while (_mutexList.Count > 0)
            {
                try
                {
                    disposalAction(_mutexList.First.Value);                    
                }
                catch (ApplicationException)
                {
                    PrintWarning($"{LoggerName} Tried to release unowned mutex!");
                }
                catch (ObjectDisposedException)
                {
                    PrintWarning($"{LoggerName} Tried to dispose a mutex that was already disposed!");
                }
                finally
                {
                    _mutexList.RemoveFirst();
                }
            }
        }

        // /// <summary>
        // /// Returns a collection containing a unique valid mutex name for every file path
        // /// potentially subject to read/write operations by this Logger instance.
        // /// </summary>
        private string[] GenerateAllPossibleFilePathsAsMutexNames()
        {
            string[] mutexNames = new string[TotalLogFiles * 2];
            int IndexOf(int logFileNumber) { return 2 * logFileNumber; }
            for (int logFileNum = 0; logFileNum < TotalLogFiles; ++logFileNum)
            {
                mutexNames[IndexOf(logFileNum)] = ConvertFilePathToMutexName(_allPossiblePersistentLogFilePaths[logFileNum]);
                mutexNames[IndexOf(logFileNum) + 1] = ConvertFilePathToMutexName(_allPossibleTemporaryLogFilePaths[logFileNum]);
            }
            return mutexNames;
        }

        /// <summary>
        /// Returns an array containing a unique valid mutex name for every file path potentially
        /// subject to read/write operations by this Logger instance.
        /// </summary>
        // private string[] GenerateAllPossibleFilePathsAsMutexNames()
        // {
        //     string[] mutexNames = new string[TotalLogFiles * 2];
        //     using(IEnumerator<string> allPersistentPathsEnumerator = (IEnumerator<string>)_allPossiblePersistentLogFilePaths.GetEnumerator())
        //     using(IEnumerator<string> allTmpPathsEnumerator = (IEnumerator<string>)_allPossibleTemporaryLogFilePaths.GetEnumerator())
        //     {
        //         int i = 0;
        //         while (i < mutexNames.Length)
        //         {
        //             allPersistentPathsEnumerator.MoveNext();
        //             mutexNames[i++] = ConvertFilePathToMutexName(allPersistentPathsEnumerator.Current);
        //             allTmpPathsEnumerator.MoveNext();
        //             mutexNames[i++] = ConvertFilePathToMutexName(allTmpPathsEnumerator.Current);
        //         }
        //         return mutexNames;
        //     }
        // }

        /// <summary>
        /// Returns a valid log file path. If 'logFileNum' is set, it will generate a log file name
        /// incorporating the number. If 'logFileType' is set to TEMPORARY, it will generate a log
        /// file name ending with the extension .tmp. By default, it will return the base log file
        /// path.
        /// </summary>
        private string GenerateLogFilePathWithExtension(LogFileType logFileType, int? logFileNum = null)
        {
            string extension = logFileType == LogFileType.TEMPORARY ? LoggerTmpFileNameExtension : LoggerFileNameExtension;
            string logFileNumStr = logFileNum?.ToString();
            int sizeOfPath = Utils.GetSizeInBytes(_baseLogFilePathNoExtension) + Utils.GetSizeInBytes(logFileNumStr) + Utils.GetSizeInBytes(extension);
            StringBuilder logFilePathBuilder = new StringBuilder(sizeOfPath);
            logFilePathBuilder.Append(_baseLogFilePathNoExtension);
            logFilePathBuilder.Append(logFileNumStr);
            logFilePathBuilder.Append(extension);
            return logFilePathBuilder.ToString();
        }

        /// <summary>
        /// Returns a collection containing every possible persistent log file path potentially
        /// subject to read/write operations by this Logger instance, starting with the base path,
        /// at index 0.
        /// </summary>
        private LogFilePath[] GenerateAllPossiblePersistentLogFilePaths()
        {
            List<LogFilePath> filePaths = new List<LogFilePath>();
            filePaths.Add(new LogFilePath(GenerateLogFilePathWithExtension(LogFileType.PERSISTENT), 0));
            for (int logFileNum = 1; logFileNum < TotalLogFiles; ++logFileNum)
            {
                filePaths.Add(new LogFilePath(GenerateLogFilePathWithExtension(LogFileType.PERSISTENT, logFileNum), logFileNum));
            }
            return filePaths.ToArray();
        }

        /// <summary>
        /// Returns a collection containing every possible temporary log file path potentially
        /// subject to read/write operations by this Logger instance, starting with the base path,
        /// at index 0.
        /// </summary>
        private LogFilePath[] GenerateAllPossibleTmpLogFilePaths()
        {
            List<LogFilePath> filePaths = new List<LogFilePath>();
            filePaths.Add(new LogFilePath(GenerateLogFilePathWithExtension(LogFileType.TEMPORARY), 0));
            for (int logFileNum = 1; logFileNum < TotalLogFiles; ++logFileNum)
            {
                filePaths.Add(new LogFilePath(GenerateLogFilePathWithExtension(LogFileType.TEMPORARY, logFileNum), logFileNum));
            }
            return filePaths.ToArray();
        }

        /// <summary>
        /// Returns a collection containing a path to every currently existing persistent log file,
        /// starting with the most recent.
        /// </summary>
        private IEnumerable<LogFilePath> GetAllExistingPersistentLogFilePaths()
        {
            foreach (LogFilePath filePath in _allPossiblePersistentLogFilePaths)
            {
                if (File.Exists(filePath))
                {
                    yield return filePath;
                }
            }
        }

        private void DeleteAllLogFiles()
        {
            foreach(string logFile in AllExistingPersistentLogFilePaths)
            {
                File.Delete(logFile);
            }
        }

        /// <summary>
        /// Selectively deletes logs from files by retaining only the logs flagged for retention in
        /// logFileDeleteInfos and performs a rollback as needed to fill in gaps due to full log
        /// file deletions.
        /// </summary>
        private void DeleteLogsFromFiles(IEnumerable<LogFileDeleteInfo> logFileDeleteInfos)
        {
            string GetLogFilePath(LogFileDeleteInfo logFileDeleteInfo) { return _allPossiblePersistentLogFilePaths[logFileDeleteInfo.LogFileIndex]; }
            string GetTmpLogFilePath(LogFileDeleteInfo logFileDeleteInfo) { return _allPossibleTemporaryLogFilePaths[logFileDeleteInfo.LogFileIndex]; }
            bool didDeleteFile = false;
            foreach (LogFileDeleteInfo logFile in logFileDeleteInfos)
            {
                if (logFile.DeleteAllLogs)
                {
                    File.Delete(GetLogFilePath(logFile));
                    didDeleteFile = true;
                }
                else if (logFile.HasLogsToDelete)
                {
                    string logFilePath = GetLogFilePath(logFile);
                    string tmpLogFilePath = GetTmpLogFilePath(logFile);
                    using (var input = new FileStream(logFilePath, FileMode.Open, FileAccess.Read))
                    using (var output = new FileStream(tmpLogFilePath, FileMode.Create, FileAccess.Write))
                    using (var reader = new StreamReader(input, Encoder))
                    {
                        ulong logIndex = 0;
                        LinkedListNode<ulong> nextLogToKeep = logFile.LogsToKeep.First;
                        for (string line = reader.ReadLine(); line != null; line = reader.ReadLine(), ++logIndex)
                        {                            
                            if (logIndex == nextLogToKeep?.Value)
                            {
                                byte[] lineBytes = Encoder.GetBytes(line + Environment.NewLine);
                                output.Write(lineBytes, 0, lineBytes.Length);
                                nextLogToKeep = nextLogToKeep.Next;
                            }
                        }
                        // Replace the original file with the file sans deleted logs
                        File.Delete(logFilePath);
                        File.Move(tmpLogFilePath, logFilePath);
                    }
                }
            }
            if (didDeleteFile)
            {
                RollbackFiles();
            }
        }

        

        private bool TryDeserializeJson<T>(string jsonString, out T deserializedJson)
        {
            try
            {
                deserializedJson = JsonSerializer.Deserialize<T>(jsonString);
                return true;
            }
            catch (JsonException)
            {
                deserializedJson = default;
                return false;
            }
        }
    }
    
    internal class LogFilePath
    {
        internal LogFilePath (string logFilePath, int logFileIndex)
        {
            Path = logFilePath;
            Index = logFileIndex;
        }

        public static implicit operator string(LogFilePath logFilePath) => logFilePath.Path;
        internal string Path { get; private set; }
        internal int Index { get; private set; }
    }

    internal class LogFileDeleteInfo
    {
        internal LogFileDeleteInfo(int logFileIndex)
        {
            LogsToKeep = new LinkedList<ulong>();
            TotalLogsInFile = 0;
            LogFileIndex = logFileIndex;
        }

        internal LinkedList<ulong> LogsToKeep { get; private set; }
        internal ulong TotalLogsInFile { get; set; }
        internal int LogFileIndex { get; private set; }
        internal bool HasLogsToDelete { get => (ulong)LogsToKeep.Count() < TotalLogsInFile; }
        internal bool DeleteAllLogs { get => (ulong)LogsToKeep.Count() == 0; }
    }
}

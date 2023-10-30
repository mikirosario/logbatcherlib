using System;
using System.Text;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;

namespace LogBatcher
{
    public class Logger
    {
        private enum Signal
        {
            LOG,
            STOP
        }
        private const string LoggerNameDefault = "LoggerGW";
        private const byte LoggerNameMaxLength = 50;
        private const byte LogFilePathMaxLength = 50;
        private const byte TotalLogFilesLowerLimit = 1;
        private const long FileSizeUpperLimit = 500000000;
        private const ushort LogDumpFrequencyLowerLimit = 1;
        private readonly ConcurrentQueue<string> _logEntries = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _logSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent _stopSignal = new AutoResetEvent(false);
        private readonly string _loggerName;
        private readonly string _baseLogFilePath;
        private long _baseLogFileSize = 0;
        private long _maxFileSizeInBytes;
        private ushort _logDumpFrequency;
        private byte _totalLogFiles;
        private volatile bool _stop = false;
        private Thread _loggingThread;

        public string LoggerName
        {
            get => _loggerName;
        }

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

        public Logger(string loggerName, string logFilePath, long maxLogFileSizeInBytes, byte totalLogFiles, ushort logDumpFrequency)
        {
            MaxFileSizeInBytes = maxLogFileSizeInBytes;
            LogDumpFrequency = logDumpFrequency;
            TotalLogFiles = totalLogFiles;
            _loggerName = ValidateLoggerName(loggerName);
            _baseLogFilePath = ValidatePathAndCreateDirectory(logFilePath);
            _baseLogFileSize = QueryFileSystemForBaseLogFileSize();
            _loggingThread = new Thread(ThreadLogQueueMonitor);
            _loggingThread.IsBackground = true;
            _loggingThread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _stopSignal.Set(); // Signal the logger thread to stop
            _loggingThread.Join(); // Wait for the logger thread to finish
            Console.WriteLine("Logger Stopped");
        }

        public void Log(string message)
        {
            string logMessage = $"{DateTime.Now}: {message.TrimEnd('\0')}";
            Console.WriteLine($"############# {LoggerName} logged: {logMessage}");
            _logEntries.Enqueue(logMessage);
            if (_logEntries.Count >= LogDumpFrequency)
            {
                _logSignal.Set();
            }
        }

        private string ValidateLoggerName(string loggerName)
        {
            string returnValue;
            if (loggerName.Length > LoggerNameMaxLength)
            {
                returnValue = loggerName.Substring(0, LoggerNameMaxLength);
                Console.WriteLine($"Invalid Logger Name: {loggerName}.");
                Console.WriteLine($"Logger Name too long (+{LoggerNameMaxLength}). Truncated to: {returnValue}.");
            }
            else
            {
                returnValue = loggerName;
            }
            if (string.IsNullOrWhiteSpace(returnValue) || loggerName.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                returnValue = LoggerNameDefault;
                Console.WriteLine($"Invalid Logger Name: {loggerName}.");
                Console.WriteLine($"Logger Name contains invalid characters. Using default Logger Name: {LoggerNameDefault}.");
            }
            return returnValue;
        }

        /// <summary>
        /// Validates log file path and creates directories indicated
        /// therein if needed.
        /// Returns validated log file path relative to the executable's
        /// directory.
        /// </summary>
        /// <remarks>
        /// Invalid file paths result in LoggerName being
        /// used as the log directory and file name. Passing a log file path
        /// without specifying a directory will result in LoggerName being used
        /// as the log directory and the log file path as the file name.
        /// 
        /// Throws all Directory.CreateDirectory exceptions when creation of the
        /// default fallback directory (the CreateDirectory call inside the
        /// catch block).
        /// </remarks>
        /// <param name="logFilePath">Desired relative path to the base log
        /// file.</param>
        /// <returns>Validated relative path to the base log file.</returns>
        /// <exception cref="IOException"></exception>
        /// <exception cref="UnauthorizedAccessException"></exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="PathTooLongException"></exception>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="NotSupportedException"></exception>
        private string ValidatePathAndCreateDirectory(string logFilePath)
        {
            string baseLogPath;
            string dir;
            string exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            try
            {
                if (string.IsNullOrWhiteSpace(logFilePath) || logFilePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    throw new ArgumentException($"Invalid {LoggerName} path {logFilePath}. Invalid characters.");
                }
                if (logFilePath.Length > LogFilePathMaxLength + 1 + LoggerNameMaxLength)
                {
                    throw new PathTooLongException($"Invalid {LoggerName} path {logFilePath}. Too long (+{LogFilePathMaxLength}).");
                }
                dir = Path.GetDirectoryName(logFilePath);
                if (dir == null)
                {
                    throw new ArgumentException($"Malformed {LoggerName} path {logFilePath}. Denotes root directory.");
                }
                if (dir.Length < 1) //Directory not provided
                {
                    dir = LoggerName;
                    baseLogPath = Path.Combine(exeDirectory, LoggerName, logFilePath);
                }
                else
                {
                    baseLogPath = Path.Combine(exeDirectory, logFilePath);
                }
                Directory.CreateDirectory(Path.Combine(exeDirectory, dir));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{LoggerName} Path Validation Error: {ex.Message}");
                Console.WriteLine($"{LoggerName} using default directory path {LoggerName}");
                Directory.CreateDirectory(LoggerName);
                baseLogPath = Path.Combine(LoggerName, LoggerName);
            }
            return baseLogPath;
        }

        private long QueryFileSystemForBaseLogFileSize()
        {
            return File.Exists(_baseLogFilePath) ? new FileInfo(_baseLogFilePath).Length : 0;
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
            while (!_stop && ThreadWaitSignal() != Signal.STOP)
            {
                ThreadWriteAllLogsToFile();
            }
            Console.WriteLine("Saving cached logs to file and closing...");
            ThreadWriteAllLogsToFile();
        }

        private void ThreadWriteAllLogsToFile()
        {
            while (_logEntries.TryDequeue(out string logEntry))
            {
                try
                {
                    logEntry += Environment.NewLine;
                    int logEntrySize = Encoding.UTF8.GetByteCount(logEntry);
                    if (IsFullBaseLogFile(logEntrySize))
                    {
                        RollFiles();
                        _baseLogFileSize = QueryFileSystemForBaseLogFileSize();
                    }
                    File.AppendAllText(_baseLogFilePath, logEntry, Encoding.UTF8);
                    _baseLogFileSize += logEntrySize;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Logging error: {ex.Message}");
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
                File.WriteAllText(_baseLogFilePath, string.Empty);
                return;
            }

            CreateTmpLogFiles();
            RolloverTmpLogFiles();
            DeletePersistentLogFiles();
            MoveTmpLogFilesToPersistentLogFiles();
            DeleteTmpLogFiles();
        }

        /// <summary>
        /// Tries to copy all existing log files to a series of temporary log
        /// files. Throws an exception if unsuccessful for any reason.
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
                if (File.Exists(_baseLogFilePath))
                {
                    File.Copy(_baseLogFilePath, $"{_baseLogFilePath}.tmp", overwrite: true);
                }
                for (int i = 0; i < TotalLogFiles - 1; ++i)
                {
                    string originalFile = $"{_baseLogFilePath}.{i}";
                    string tmpFile = $"{_baseLogFilePath}.{i}.tmp";
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
        /// base_log_file.tmp becomes log_file_0.tmp, log_file_0.tmp becomes
        /// log_file_1.tmp, etc., until the total number of temporary log files
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
                int highestLogFileIndex = TotalLogFiles - 2; //-1 for counting from 0, -1 for unnumbered base file.
                string lastPossibleFilePath = $"{_baseLogFilePath}.{highestLogFileIndex}.tmp";
                if (File.Exists(lastPossibleFilePath))
                {
                    File.Delete(lastPossibleFilePath);
                }
                for (int logFileIndex = highestLogFileIndex - 1; logFileIndex >= 0; --logFileIndex)
                {
                    string targetFile = $"{_baseLogFilePath}.{logFileIndex}.tmp";
                    string rollToFile = $"{_baseLogFilePath}.{logFileIndex + 1}.tmp";

                    if (File.Exists(targetFile))
                    {
                        if (File.Exists(rollToFile))
                        {
                            File.Delete(rollToFile);
                        }
                        File.Move(targetFile, rollToFile);
                    }
                }
                if (File.Exists($"{_baseLogFilePath}.tmp"))
                {
                    File.Move($"{_baseLogFilePath}.tmp", $"{_baseLogFilePath}.0.tmp");
                }

                File.WriteAllText($"{_baseLogFilePath}.tmp", string.Empty, new UTF8Encoding(true));
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
            if (File.Exists($"{_baseLogFilePath}.tmp"))
            {
                File.Delete($"{ _baseLogFilePath}.tmp");
            }
            for (int i = 0; i < TotalLogFiles - 1; ++i)
            {
                string tmpFile = $"{_baseLogFilePath}.{i}.tmp";
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
            if (File.Exists($"{_baseLogFilePath}"))
            {
                File.Delete($"{_baseLogFilePath}");
            }
            for (int i = 0; i < TotalLogFiles - 1; ++i)
            {
                string persistentFile = $"{_baseLogFilePath}.{i}";
                if (File.Exists(persistentFile))
                {
                    File.Delete(persistentFile);
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
            if (File.Exists($"{_baseLogFilePath}.tmp"))
            {
                File.Move($"{_baseLogFilePath}.tmp", _baseLogFilePath);
            }
            for (int i = 0; i < TotalLogFiles - 1; ++i)
            {
                string tmpFile = $"{_baseLogFilePath}.{i}.tmp";
                string persistentFile = $"{_baseLogFilePath}.{i}";
                if (File.Exists(tmpFile))
                {
                    File.Move(tmpFile, persistentFile);
                }
            }
        }
    }
}

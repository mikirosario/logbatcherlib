using System;

namespace LogBatcher
{
    public class FileAccessConflictException : Exception
    {
        public FileAccessConflictException(string message) : base(message) { }
    }
}
